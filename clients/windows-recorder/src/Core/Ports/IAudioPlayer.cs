namespace Whisper.Recorder.Core.Ports;

public interface IAudioPlayer : IAsyncDisposable
{
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }

    ValueTask OpenAsync(string wavPath, CancellationToken cancellationToken = default);
    ValueTask PlayAsync(CancellationToken cancellationToken = default);
    ValueTask PauseAsync(CancellationToken cancellationToken = default);
    ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
