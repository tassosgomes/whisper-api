using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Core.Tracking;

namespace Whisper.Recorder.Core.Recording;

public enum RecordingState { Recording, Saved, Interrupted, Uploading, Processing, Transcribed, Failed, [System.Text.Json.Serialization.JsonStringEnumMemberName("result-expired")] ResultExpired }
public enum RecordingWarning { DeviceLost, LowDiskSpace, DiskFull, LimitApproaching, ServiceLimitReached, CaptureFailed }

public sealed record RecordingMetadata(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    RecordingState State,
    TimeSpan Duration,
    AudioSourceKind[] Sources,
    RecordingWarning[] Warnings,
    int UploadAttempt = 0,
    string? UploadId = null,
    string? JobId = null,
    bool PutCompleted = false,
    bool UploadRejected = false,
    UploadProblem UploadProblem = UploadProblem.None,
    DateTimeOffset? UploadRetryAt = null,
    string? PublicJobStatus = null,
    string? FailureCode = null,
    TrackingProblem TrackingProblem = TrackingProblem.None,
    DateTimeOffset? NextPollAt = null,
    int PollDelaySeconds = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : Title;
}

public interface IDiskSpace
{
    long AvailableBytes(string directory);
}

public sealed class LocalDiskSpace : IDiskSpace
{
    public long AvailableBytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}
