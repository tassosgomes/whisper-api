using System.Globalization;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Diagnostics;

public enum DiagnosticEvent
{
    ApplicationStarted,
    ApplicationStopped,
    CaptureStarted,
    CaptureStopped,
    AudioDeviceLost,
    AudioDeviceRestored,
    NetworkUnavailable,
    RequestFailed
}

/// <summary>
/// Only a timestamp and an allowlisted event can cross this boundary. No free text,
/// exceptions, paths, HTTP details, keys, audio or transcription are accepted.
/// The caller owns the writer and its lifetime.
/// </summary>
public sealed class DiagnosticLog(TextWriter writer, IClock clock)
{
    private readonly object sync = new();

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        if (!Enum.IsDefined(diagnosticEvent))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticEvent));
        }

        lock (sync)
        {
            writer.WriteLine($"{clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {diagnosticEvent}");
            writer.Flush();
        }
    }
}
