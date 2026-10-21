namespace Whisper.Recorder.Core.Ports;

public enum AudioSourceKind
{
    Microphone,
    SystemLoopback
}

public sealed record AudioDevice(string Id, string DisplayName, AudioSourceKind Source, bool IsDefault);

/// <summary>Interleaved signed 16-bit PCM; CapturedAt is the timestamp of the first frame.</summary>
public sealed record AudioPcmBlock(
    AudioSourceKind Source,
    DateTimeOffset CapturedAt,
    int SampleRate,
    int Channels,
    ReadOnlyMemory<short> Samples);

/// <summary>Peak is normalized to the range 0..1.</summary>
public sealed record AudioLevel(AudioSourceKind Source, float Peak);

public sealed record AudioDeviceAvailability(AudioDevice Device, bool IsAvailable);

/// <summary>The host owns device handles and block buffers; samples stay valid after delivery.</summary>
public interface IAudioCapture : IAsyncDisposable
{
    event Action<AudioPcmBlock>? BlockAvailable;
    event Action<AudioLevel>? LevelChanged;
    event Action<AudioDeviceAvailability>? DeviceAvailabilityChanged;

    ValueTask<IReadOnlyList<AudioDevice>> ListDevicesAsync(
        AudioSourceKind source, CancellationToken cancellationToken = default);

    ValueTask StartAsync(AudioDevice device, CancellationToken cancellationToken = default);
    ValueTask StopAsync(AudioSourceKind source, CancellationToken cancellationToken = default);
}
