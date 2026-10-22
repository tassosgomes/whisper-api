using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Api.Generated.Models;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Storage;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Core.Tracking;

/// <summary>One shared tracker per application. RunAsync resumes durable work and follows new accepted uploads.</summary>
public sealed class JobTracker(RecordingRepository recordings, ApiConfigurationStore configuration,
    ICredentialVault vault, TrackingTransport transport, UploadTransport uploadTransport,
    IConnectivity connectivity, IClock clock, IScheduler scheduler, INotifier notifier)
{
    private int running;
    private readonly Dictionary<Guid, TrackingStatus> published = [];
    public event Action<TrackingStatus>? Changed;

    public TrackingStatus Status(Guid id)
    {
        var recording = recordings.Read(id);
        return new(recording, Active(recording) && (!connectivity.IsConnected || recording.TrackingProblem == TrackingProblem.Network));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Tracker already running.");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await scheduler.DelayAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    /// <summary>One due-work pass; also useful with a controlled clock. No audio is ever uploaded here.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var recording in recordings.List().Where(Active))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publish(recording);
                if (!connectivity.IsConnected || recording.NextPollAt > clock.UtcNow) continue;
                await PollAsync(recording, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { recordings.Operations.Release(); }
    }

    private static bool Active(RecordingMetadata recording) => recording.State is RecordingState.Uploading or RecordingState.Processing;

    private async Task PollAsync(RecordingMetadata recording, CancellationToken cancellationToken)
    {
        try
        {
            // An upload interrupted before PUT finished still needs a manual send.
            // A durable completed PUT can safely recover a lost complete response.
            if (string.IsNullOrWhiteSpace(recording.JobId) && (recording.TrackingProblem == TrackingProblem.SendRequired
                || !recording.PutCompleted || string.IsNullOrWhiteSpace(recording.UploadId)))
            {
                if (recording.TrackingProblem != TrackingProblem.SendRequired)
                    Save(recording with { TrackingProblem = TrackingProblem.SendRequired });
                return;
            }
            var address = configuration.Read();
            var key = await vault.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (address is null || string.IsNullOrWhiteSpace(key))
            {
                Retry(recording, TrackingProblem.ConfigurationRequired);
                return;
            }
            if (string.IsNullOrWhiteSpace(recording.JobId))
            {
                var jobId = await uploadTransport.CompleteAsync(address, key, recording.UploadId!, cancellationToken).ConfigureAwait(false);
                recording = recording with { State = RecordingState.Processing, JobId = jobId };
                Save(recording);
            }
            var job = await transport.GetJobAsync(address, key, recording.JobId!, cancellationToken).ConfigureAwait(false);
            recording = recording with { State = RecordingState.Processing, PublicJobStatus = job.Status?.ToString().ToLowerInvariant() };
            switch (job.Status)
            {
                case TranscriptionStatus.Completed:
                    if (!connectivity.IsConnected) { Publish(recording); return; }
                    var json = await transport.GetResultAsync(address, key, recording.JobId!, cancellationToken).ConfigureAwait(false);
                    var problem = StoreResult(recording, json);
                    await FinishAsync(recording with { State = RecordingState.Transcribed, TrackingProblem = problem }, cancellationToken).ConfigureAwait(false);
                    break;
                case TranscriptionStatus.Failed:
                    await FinishAsync(recording with
                    {
                        State = RecordingState.Failed,
                        FailureCode = job.Failure?.Code?.ToString() ?? "TRANSCRIPTION_FAILED",
                        TrackingProblem = TrackingProblem.None
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case TranscriptionStatus.Downloading:
                case TranscriptionStatus.Queued:
                case TranscriptionStatus.Processing:
                    var delay = Math.Min(30, Math.Max(2, recording.PollDelaySeconds * 2));
                    Save(recording with { TrackingProblem = TrackingProblem.None, PollDelaySeconds = delay, NextPollAt = clock.UtcNow.AddSeconds(delay) });
                    break;
                default:
                    Retry(recording, TrackingProblem.Service);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ApiException error)
        {
            // Only an accepted job's 404 means its retained result is unavailable.
            if (error.ResponseStatusCode == 404 && !string.IsNullOrWhiteSpace(recording.JobId))
                await FinishAsync(recording with { State = RecordingState.ResultExpired, TrackingProblem = TrackingProblem.None }, cancellationToken).ConfigureAwait(false);
            else if (error.ResponseStatusCode is 404 or 410 or 422 && string.IsNullOrWhiteSpace(recording.JobId))
                Save(recording with { TrackingProblem = TrackingProblem.SendRequired, NextPollAt = null, PollDelaySeconds = 0 });
            else
                Retry(recording, error.ResponseStatusCode is 401 or 403 ? TrackingProblem.ConfigurationRequired : TrackingProblem.Service);
        }
        catch (HttpRequestException) { Retry(recording, TrackingProblem.Network); }
        catch (OperationCanceledException) { Retry(recording, TrackingProblem.Network); }
        catch (InvalidDataException) { Retry(recording, TrackingProblem.Service); }
        catch (IOException) { Retry(recording, TrackingProblem.Storage); }
        catch (UnauthorizedAccessException) { Retry(recording, TrackingProblem.Storage); }
        catch (JsonException) { Retry(recording, TrackingProblem.Service); }
        catch (InvalidOperationException) { Retry(recording, TrackingProblem.Service); }
        catch (FormatException) { Retry(recording, TrackingProblem.Service); }
        catch (KeyNotFoundException) { Retry(recording, TrackingProblem.Service); }
    }

    private void Retry(RecordingMetadata recording, TrackingProblem problem)
    {
        var delay = Math.Min(60, Math.Max(10, recording.PollDelaySeconds * 2));
        Save(recording with { TrackingProblem = problem, PollDelaySeconds = delay, NextPollAt = clock.UtcNow.AddSeconds(delay) });
    }

    private async Task FinishAsync(RecordingMetadata recording, CancellationToken cancellationToken)
    {
        Save(recording with { NextPollAt = null, PollDelaySeconds = 0 });
        try
        {
            await notifier.NotifyAsync(recording.State == RecordingState.Transcribed
                ? RecorderNotification.TranscriptionCompleted : RecorderNotification.TranscriptionFailed, cancellationToken, recording.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // A host notification failure must never undo a durable local result or cause repolling.
        catch (Exception) { }
    }

    private TrackingProblem StoreResult(RecordingMetadata recording, byte[] json)
    {
        var directory = recordings.DirectoryFor(recording.Id);
        WriteDurable(Path.Combine(directory, "result-v1.json"), json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number))
            throw new JsonException();
        if (number != 1) return TrackingProblem.UpdateRequired;
        var text = new StringBuilder();
        var hasSpeech = false;
        foreach (var segment in root.GetProperty("segments").EnumerateArray())
        {
            var milliseconds = segment.GetProperty("startMs").GetInt64();
            if (milliseconds < 0 || milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) throw new JsonException();
            var time = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
            var content = segment.GetProperty("text").GetString() ?? "";
            hasSpeech |= !string.IsNullOrWhiteSpace(content);
            text.Append(CultureInfo.InvariantCulture, $"[{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}] ");
            text.AppendLine(content.Replace('\r', ' ').Replace('\n', ' ').Trim());
        }
        WriteDurable(Path.Combine(directory, "transcription.txt"), Encoding.UTF8.GetBytes(text.ToString()));
        return hasSpeech ? TrackingProblem.None : TrackingProblem.EmptyTranscript;
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        AtomicFile.Replace(temporary, path);
    }

    private void Save(RecordingMetadata recording)
    {
        recordings.Save(recording);
        Publish(recording);
    }

    private void Publish(RecordingMetadata recording)
    {
        var status = new TrackingStatus(recording, Active(recording) && (!connectivity.IsConnected || recording.TrackingProblem == TrackingProblem.Network));
        if (published.TryGetValue(recording.Id, out var previous) && previous.IsOffline == status.IsOffline
            && previous.Recording.Sources.SequenceEqual(recording.Sources)
            && previous.Recording.Warnings.SequenceEqual(recording.Warnings)
            && (previous.Recording with { Sources = recording.Sources, Warnings = recording.Warnings }) == recording) return;
        published[recording.Id] = status;
        Changed?.Invoke(status);
    }
}
