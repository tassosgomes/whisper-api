using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Recording;

public enum RecordingState { Recording, Saved, Interrupted }
public enum RecordingWarning { DeviceLost, LowDiskSpace, DiskFull, LimitApproaching, ServiceLimitReached, CaptureFailed }

public sealed record RecordingMetadata(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    RecordingState State,
    TimeSpan Duration,
    AudioSourceKind[] Sources,
    RecordingWarning[] Warnings);

public interface IDiskSpace
{
    long AvailableBytes(string directory);
}

public sealed class LocalDiskSpace : IDiskSpace
{
    public long AvailableBytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}
