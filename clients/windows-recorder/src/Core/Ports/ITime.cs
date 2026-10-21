namespace Whisper.Recorder.Core.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IScheduler
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SystemScheduler : IScheduler
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        new(Task.Delay(delay, cancellationToken));
}
