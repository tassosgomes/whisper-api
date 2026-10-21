using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Kiota.Abstractions;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Api.Generated.Models;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;

namespace Whisper.Recorder.Core.Uploading;

/// <summary>Each invocation is one explicit user action. Reopening the app never starts an upload.</summary>
public sealed class UploadCoordinator(RecordingRepository recordings, ApiConfigurationStore configuration,
    ICredentialVault vault, UploadTransport transport, IClock clock, DiagnosticLog? diagnostic = null)
{
    // Also prevents double clicks through different presentation objects sharing this coordinator.
    private readonly SemaphoreSlim operations = recordings.Operations;
    public event Action? Changed;

    public UploadConfirmation Prepare(Guid id)
    {
        var recording = recordings.Read(id);
        return new(id, recording.DisplayTitle, recording.Duration, new FileInfo(recordings.AudioPath(id)).Length,
            recording.State == RecordingState.Transcribed);
    }

    public async Task<UploadResult> ConfirmAndSendAsync(UploadConfirmation confirmation,
        bool existingTranscriptionConfirmed = false, IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        RecordingMetadata? recording = null;
        var completing = false;
        try
        {
            recording = recordings.Read(confirmation.RecordingId);
            if (recording.State == RecordingState.Recording || Prepare(recording.Id) != confirmation
                || (confirmation.HasTranscription && !existingTranscriptionConfirmed))
                return new(UploadProblem.ConfirmationRequired);
            if (recording.State == RecordingState.Processing)
                return new(UploadProblem.AlreadyProcessing);
            if (recording.UploadRetryAt > clock.UtcNow)
                return new(UploadProblem.RetryLater, recording.UploadRetryAt);

            var address = configuration.Read();
            var key = await vault.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (address is null || string.IsNullOrWhiteSpace(key))
                return new(UploadProblem.ConfigurationRequired);

            // Keep this handle open from hashing through PUT. No large in-memory audio copy.
            await using var audio = new FileStream(recordings.AudioPath(recording.Id), FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (audio.Length != confirmation.SizeBytes) return new(UploadProblem.ConfirmationRequired);
            var checksum = Convert.ToBase64String(await SHA256.HashDataAsync(audio, cancellationToken).ConfigureAwait(false));
            if (recording.UploadAttempt == 0 || recording.UploadRejected || confirmation.HasTranscription
                || recording.State is RecordingState.Failed or RecordingState.ResultExpired)
                recording = recording with
                {
                    UploadAttempt = checked(recording.UploadAttempt + 1),
                    UploadId = null,
                    JobId = null,
                    PutCompleted = false,
                    UploadRejected = false,
                    PublicJobStatus = null,
                    FailureCode = null,
                    TrackingProblem = TrackingProblem.None,
                    NextPollAt = null,
                    PollDelaySeconds = 0
                };
            recording = recording with { State = RecordingState.Uploading, UploadProblem = UploadProblem.None, UploadRetryAt = null };
            Save(recording); // Commit the attempt before any remote side effect.
            var body = new CreateTranscriptionUploadRequest
            {
                ContentType = ContentType.AudioWav,
                SizeBytes = audio.Length,
                ChecksumSha256 = checksum,
                DurationMs = (long)recording.Duration.TotalMilliseconds,
                ClientReference = recording.Id.ToString("D")
            };
            var idempotencyKey = $"recording-{recording.Id:D}-attempt-{recording.UploadAttempt.ToString(CultureInfo.InvariantCulture)}";
            var upload = await transport.CreateAsync(address, key, idempotencyKey, body, cancellationToken).ConfigureAwait(false);
            recording = ApplyUpload(recording, upload);
            Save(recording);
            // A lost complete response may already have created the job, even hours earlier.
            if (!string.IsNullOrWhiteSpace(upload.JobId)) return Accept(recording, upload.JobId);

            if (!recording.PutCompleted)
            {
                var status = await transport.PutAsync(upload.Upload ?? throw new InvalidDataException(), audio, progress, cancellationToken).ConfigureAwait(false);
                if (status == HttpStatusCode.Forbidden)
                {
                    // A signed URL can expire between create and PUT. Renew at most once,
                    // with the same key; all other failures await another manual action.
                    upload = await transport.CreateAsync(address, key, idempotencyKey, body, cancellationToken).ConfigureAwait(false);
                    recording = ApplyUpload(recording, upload);
                    Save(recording);
                    if (!string.IsNullOrWhiteSpace(upload.JobId)) return Accept(recording, upload.JobId);
                    status = await transport.PutAsync(upload.Upload ?? throw new InvalidDataException(), audio, progress, cancellationToken).ConfigureAwait(false);
                }
                if ((int)status < 200 || (int)status >= 300) return Fail(recording, UploadProblem.Network);
                recording = recording with { PutCompleted = true };
                Save(recording);
            }
            completing = true;
            var jobId = await transport.CompleteAsync(address, key, recording.UploadId!, cancellationToken).ConfigureAwait(false);
            return Accept(recording, jobId);
        }
        catch (ApiException error)
        {
            var code = (error as Problem)?.Code;
            var problem = error.ResponseStatusCode switch
            {
                401 => UploadProblem.KeyRejected,
                413 => UploadProblem.SizeLimit,
                429 => UploadProblem.RetryLater,
                404 => UploadProblem.NotFound,
                _ => code switch
                {
                    "UNSUPPORTED_MEDIA_FORMAT" => UploadProblem.UnsupportedFormat,
                    "MEDIA_DURATION_LIMIT_EXCEEDED" => UploadProblem.DurationLimit,
                    "UPLOAD_CHECKSUM_MISMATCH" => UploadProblem.ChecksumMismatch,
                    "UPLOAD_SIZE_MISMATCH" => UploadProblem.SizeMismatch,
                    "UPLOAD_REJECTED" => UploadProblem.Rejected,
                    "UPLOAD_NOT_RECEIVED" => UploadProblem.NotReceived,
                    "IDEMPOTENCY_KEY_REUSED" => UploadProblem.Conflict,
                    _ => UploadProblem.Service
                }
            };
            DateTimeOffset? retryAt = problem == UploadProblem.RetryLater ? RetryAt(error) : null;
            return Fail(recording, problem, retryAt,
                rejected: (completing && error.ResponseStatusCode == 422) || code == "UPLOAD_REJECTED");
        }
        catch (OperationCanceledException) { return Fail(recording, cancellationToken.IsCancellationRequested ? UploadProblem.Cancelled : UploadProblem.Network); }
        catch (HttpRequestException) { return Fail(recording, UploadProblem.Network); }
        catch (IOException) { return Fail(recording, UploadProblem.Storage); }
        catch (UnauthorizedAccessException) { return Fail(recording, UploadProblem.Storage); }
        catch (Exception) { return Fail(recording, UploadProblem.Service); }
        finally { operations.Release(); }
    }

    private static RecordingMetadata ApplyUpload(RecordingMetadata recording, TranscriptionUpload upload)
    {
        if (string.IsNullOrWhiteSpace(upload.UploadId)) throw new InvalidDataException();
        return recording with { UploadId = upload.UploadId, PutCompleted = recording.UploadId == upload.UploadId && recording.PutCompleted };
    }

    private UploadResult Accept(RecordingMetadata recording, string jobId)
    {
        Save(recording with { State = RecordingState.Processing, JobId = jobId, UploadProblem = UploadProblem.None, UploadRetryAt = null });
        return new(UploadProblem.None);
    }

    private UploadResult Fail(RecordingMetadata? recording, UploadProblem problem, DateTimeOffset? retryAt = null, bool rejected = false)
    {
        diagnostic?.Write(problem == UploadProblem.Network ? DiagnosticEvent.NetworkUnavailable : DiagnosticEvent.RequestFailed);
        if (recording is not null)
        {
            try
            {
                Save(recording with
                {
                    UploadProblem = problem,
                    UploadRetryAt = retryAt,
                    UploadRejected = recording.UploadRejected || rejected,
                    PutCompleted = problem != UploadProblem.NotReceived && recording.PutCompleted
                });
            }
            catch (IOException) { return new(UploadProblem.Storage); }
            catch (UnauthorizedAccessException) { return new(UploadProblem.Storage); }
        }
        return new(problem, retryAt);
    }

    private DateTimeOffset RetryAt(ApiException error)
    {
        var value = error.ResponseHeaders?.FirstOrDefault(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return clock.UtcNow.AddSeconds(Math.Min(seconds, (DateTimeOffset.MaxValue - clock.UtcNow).TotalSeconds - 1));
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date > clock.UtcNow)
            return date;
        return clock.UtcNow.AddMinutes(1);
    }

    private void Save(RecordingMetadata recording)
    {
        recordings.Save(recording);
        Changed?.Invoke();
    }
}
