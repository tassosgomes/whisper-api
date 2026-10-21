using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Core.Recording;

public enum RecordingState { Recording, Saved, Interrupted, Uploading, Processing, Transcribed }
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
    DateTimeOffset? UploadRetryAt = null);

public interface IDiskSpace
{
    long AvailableBytes(string directory);
}

public sealed class LocalDiskSpace : IDiskSpace
{
    public long AvailableBytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}
