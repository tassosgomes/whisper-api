namespace Whisper.Recorder.Core.Ports;

public enum RecorderNotification
{
    TranscriptionCompleted,
    TranscriptionFailed,
    RecordingLimitApproaching,
    LowDiskSpace,
    AudioDeviceLost,
    RecordingStarted
}

/// <summary>The host supplies static localized text; notifications never carry transcription text.</summary>
public interface INotifier
{
    ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default);
    ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default);
}
