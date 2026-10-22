namespace Whisper.Recorder.Host.Audio;

/// <summary>
/// O WASAPI não carimba os pacotes. O carimbo do primeiro frame vem do relógio de parede na chegada
/// (o bloco termina quando chega) e da contagem de frames, que dá blocos contínuos sem o ruído da
/// chegada. Se a chegada se afastar da contagem além da tolerância (loopback que ficou em silêncio,
/// deriva do relógio do dispositivo), o carimbo é reancorado na chegada.
/// </summary>
public sealed class BlockTimestamper(int sampleRate, TimeSpan? tolerance = null)
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan limit = tolerance ?? DefaultTolerance;
    private DateTimeOffset? expected;

    public DateTimeOffset Stamp(DateTimeOffset arrival, int frames)
    {
        var length = TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / sampleRate);
        var observed = arrival - length;
        if (expected is not { } start || (start - observed).Duration() > limit)
            start = observed;
        expected = start + length;
        return start;
    }
}
