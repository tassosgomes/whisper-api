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
    private readonly Dictionary<Guid, DateTimeOffset> memoryBackoff = [];
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
        // The shared lock is held only to read the list and to save; network calls run without it, so sending or
        // removing a recording is never delayed by a slow request.
        RecordingMetadata[] active;
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active = recordings.ListReadable().Where(Active).ToArray();
            foreach (var recording in active) Publish(recording);
        }
        finally { recordings.Operations.Release(); }
        foreach (var recording in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!connectivity.IsConnected || recording.NextPollAt > clock.UtcNow) continue;
            if (memoryBackoff.TryGetValue(recording.Id, out var until) && until > clock.UtcNow) continue;
            try { await PollAsync(recording, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            // One problematic recording (unreadable file, full disk, removed meanwhile) never stops the others.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or StaleRecordingException)
            {
                if (error is not StaleRecordingException)
                    memoryBackoff[recording.Id] = clock.UtcNow.AddSeconds(Math.Min(60, Math.Max(10, recording.PollDelaySeconds * 2)));
            }
        }
    }

    /// <summary>The recording changed (sent again, removed) while the network call was in flight.</summary>
    private sealed class StaleRecordingException : Exception;

    private static bool Active(RecordingMetadata recording) => recording.State is RecordingState.Uploading or RecordingState.Processing;

    private async Task PollAsync(RecordingMetadata origin, CancellationToken cancellationToken)
    {
        var recording = origin;
        try
        {
            // An upload interrupted before PUT finished still needs a manual send.
            // A durable completed PUT can safely recover a lost complete response.
            if (string.IsNullOrWhiteSpace(recording.JobId) && (recording.TrackingProblem == TrackingProblem.SendRequired
                || !recording.PutCompleted || string.IsNullOrWhiteSpace(recording.UploadId)))
            {
                if (recording.TrackingProblem != TrackingProblem.SendRequired)
                    await SaveAsync(origin, recording with { TrackingProblem = TrackingProblem.SendRequired }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var address = configuration.Read();
            var key = await vault.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (address is null || string.IsNullOrWhiteSpace(key))
            {
                await RetryAsync(origin, recording, TrackingProblem.ConfigurationRequired, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(recording.JobId))
            {
                var jobId = await uploadTransport.CompleteAsync(address, key, recording.UploadId!, cancellationToken).ConfigureAwait(false);
                recording = recording with { State = RecordingState.Processing, JobId = jobId };
                await SaveAsync(origin, recording, cancellationToken).ConfigureAwait(false);
            }
            var job = await transport.GetJobAsync(address, key, recording.JobId!, cancellationToken).ConfigureAwait(false);
            recording = recording with { State = RecordingState.Processing, PublicJobStatus = job.Status?.ToString().ToLowerInvariant() };
            switch (job.Status)
            {
                case TranscriptionStatus.Completed:
                    if (!connectivity.IsConnected) { Publish(recording); return; }
                    var json = await transport.GetResultAsync(address, key, recording.JobId!, cancellationToken).ConfigureAwait(false);
                    var problem = StoreResult(recording, json);
                    await FinishAsync(origin, recording with { State = RecordingState.Transcribed, TrackingProblem = problem }, cancellationToken).ConfigureAwait(false);
                    break;
                case TranscriptionStatus.Failed:
                    await FinishAsync(origin, recording with
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
                    await SaveAsync(origin, recording with { TrackingProblem = TrackingProblem.None, PollDelaySeconds = delay, NextPollAt = clock.UtcNow.AddSeconds(delay) }, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (StaleRecordingException) { throw; }
        catch (ApiException error)
        {
            // Only an accepted job's 404 means its retained result is unavailable.
            if (error.ResponseStatusCode == 404 && !string.IsNullOrWhiteSpace(recording.JobId))
                await FinishAsync(origin, recording with { State = RecordingState.ResultExpired, TrackingProblem = TrackingProblem.None }, cancellationToken).ConfigureAwait(false);
            else if (error.ResponseStatusCode is 404 or 410 or 422 && string.IsNullOrWhiteSpace(recording.JobId))
                await SaveAsync(origin, recording with { TrackingProblem = TrackingProblem.SendRequired, NextPollAt = null, PollDelaySeconds = 0 }, cancellationToken).ConfigureAwait(false);
            else
                await RetryAsync(origin, recording, error.ResponseStatusCode is 401 or 403 ? TrackingProblem.ConfigurationRequired : TrackingProblem.Service, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) { await RetryAsync(origin, recording, TrackingProblem.Network, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { await RetryAsync(origin, recording, TrackingProblem.Network, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { await RetryAsync(origin, recording, TrackingProblem.Storage, cancellationToken).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { await RetryAsync(origin, recording, TrackingProblem.Storage, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false); }
        catch (FormatException) { await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false); }
        catch (KeyNotFoundException) { await RetryAsync(origin, recording, TrackingProblem.Service, cancellationToken).ConfigureAwait(false); }
    }

    /// <summary>Best effort: when even the failure cannot be saved (full disk), back off in memory and keep going.</summary>
    private async Task RetryAsync(RecordingMetadata origin, RecordingMetadata recording, TrackingProblem problem, CancellationToken cancellationToken)
    {
        var delay = Math.Min(60, Math.Max(10, recording.PollDelaySeconds * 2));
        try
        {
            await SaveAsync(origin, recording with { TrackingProblem = problem, PollDelaySeconds = delay, NextPollAt = clock.UtcNow.AddSeconds(delay) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            memoryBackoff[recording.Id] = clock.UtcNow.AddSeconds(delay);
        }
    }

    private async Task FinishAsync(RecordingMetadata origin, RecordingMetadata recording, CancellationToken cancellationToken)
    {
        await SaveAsync(origin, recording with { NextPollAt = null, PollDelaySeconds = 0 }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Applies the tracker-owned fields to what is on disk now, under the shared lock, and only if the recording is still
    /// the same attempt and job. A concurrent send, rename or removal is never overwritten.
    /// </summary>
    private async Task SaveAsync(RecordingMetadata origin, RecordingMetadata updated, CancellationToken cancellationToken)
    {
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecordingMetadata current;
            try { current = recordings.Read(origin.Id); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw new StaleRecordingException(); }
            if (!Active(current) || current.UploadAttempt != origin.UploadAttempt || current.UploadId != origin.UploadId
                || (current.JobId is not null && current.JobId != updated.JobId))
                throw new StaleRecordingException();
            var merged = current with
            {
                State = updated.State,
                JobId = updated.JobId,
                PublicJobStatus = updated.PublicJobStatus,
                FailureCode = updated.FailureCode,
                TrackingProblem = updated.TrackingProblem,
                NextPollAt = updated.NextPollAt,
                PollDelaySeconds = updated.PollDelaySeconds
            };
            recordings.Save(merged);
            memoryBackoff.Remove(origin.Id);
            Publish(merged);
        }
        finally { recordings.Operations.Release(); }
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
