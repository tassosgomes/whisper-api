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

/// <summary>
/// The host supplies static localized text; notifications never carry transcription text. When a notification refers to a
/// recording (completion/failure), <c>recordingId</c> lets the host open that recording when the user taps it.
/// </summary>
public interface INotifier
{
    ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default, Guid? recordingId = null);
    ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default);
}
