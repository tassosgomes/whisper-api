using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Tests.Doubles;

public sealed class FakeAudioCapture : IAudioCapture
{
    public List<AudioDevice> Devices { get; } = [];
    public HashSet<AudioSourceKind> ActiveSources { get; } = [];
    public AudioSourceKind? DeniedSource { get; set; }
    public event Action<AudioPcmBlock>? BlockAvailable;
    public event Action<AudioLevel>? LevelChanged;
    public event Action<AudioDeviceAvailability>? DeviceAvailabilityChanged;

    public ValueTask<IReadOnlyList<AudioDevice>> ListDevicesAsync(
        AudioSourceKind source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AudioDevice>>(Devices.Where(d => d.Source == source).ToArray());
    }

    public ValueTask StartAsync(AudioDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSources.Add(device.Source);
        if (device.Source == DeniedSource)
            throw new UnauthorizedAccessException("Test capture permission denied.");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(AudioSourceKind source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveSources.Remove(source);
        return ValueTask.CompletedTask;
    }

    public void EmitBlock(AudioPcmBlock block) => BlockAvailable?.Invoke(block);
    public void EmitLevel(AudioLevel level) => LevelChanged?.Invoke(level);
    public void EmitAvailability(AudioDeviceAvailability availability) => DeviceAvailabilityChanged?.Invoke(availability);

    public ValueTask DisposeAsync()
    {
        ActiveSources.Clear();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeCredentialVault : ICredentialVault
{
    private string? apiKey;

    public ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        apiKey = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(apiKey);
    }

    public ValueTask RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        apiKey = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeNotifier : INotifier
{
    public List<RecorderNotification> Notifications { get; } = [];
    public bool IsRecording { get; private set; }

    public ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Notifications.Add(notification);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsRecording = isRecording;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeAudioPlayer : IAudioPlayer
{
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; set; }
    public bool IsPlaying { get; private set; }
    public string? OpenedPath { get; private set; }

    public ValueTask OpenAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenedPath = wavPath;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsPlaying = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsPlaying = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Position = position;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsPlaying = false;
        Position = TimeSpan.Zero;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => StopAsync();
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
}

public sealed class FakeScheduler : IScheduler
{
    public List<TimeSpan> RequestedDelays { get; } = [];

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedDelays.Add(delay);
        return ValueTask.CompletedTask;
    }
}
