using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Host.Audio;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>Fonte WASAPI sintética: o teste entrega os bytes e dispara a parada do dispositivo.</summary>
public sealed class SyntheticCaptureSource(CaptureFormat format, AudioSourceKind kind) : ICaptureSource
{
    public CaptureFormat Format { get; } = format;
    public AudioSourceKind Kind { get; } = kind;
    public bool IsStarted { get; private set; }
    public bool IsDisposed { get; private set; }
    public event Action<byte[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    public void Start() => IsStarted = true;
    public void Stop() => IsStarted = false;
    public void Dispose() => IsDisposed = true;

    public void Emit(byte[] bytes) => DataAvailable?.Invoke(bytes, bytes.Length);
    public void Fail(Exception? error) => Stopped?.Invoke(error);
}

public sealed class SyntheticCatalog : ICaptureDeviceCatalog
{
    private readonly object sync = new();
    public static readonly AudioDevice Microphone = new("mic-1", "Microfone sintético", AudioSourceKind.Microphone, true);
    public static readonly AudioDevice Speakers = new("out-1", "Saída sintética", AudioSourceKind.SystemLoopback, true);

    public List<AudioDevice> Devices { get; } = [Microphone, Speakers];
    public HashSet<string> DeniedIds { get; } = [];
    public Exception? OpenFailure { get; set; }
    public List<SyntheticCaptureSource> Opened { get; } = [];
    public CaptureFormat MicrophoneFormat { get; set; } = new(48000, 2, 32, true);
    public CaptureFormat LoopbackFormat { get; set; } = new(44100, 2, 16, false);
    public event Action<string, bool>? DeviceAvailabilityChanged;
    public event Action? DevicesChanged;

    public IReadOnlyList<AudioDevice> List(AudioSourceKind source)
    {
        lock (sync)
            return Devices.Where(d => d.Source == source).ToArray();
    }

    public AudioDevice? GetDefault(AudioSourceKind source) => List(source).FirstOrDefault(d => d.IsDefault);

    public ICaptureSource Open(AudioDevice device)
    {
        if (OpenFailure is not null)
            throw OpenFailure;
        if (DeniedIds.Contains(device.Id))
            throw new UnauthorizedAccessException();
        lock (sync)
        {
            if (!Devices.Any(d => d.Id == device.Id))
                throw new InvalidOperationException("Dispositivo inativo.");
            var source = new SyntheticCaptureSource(device.Source == AudioSourceKind.Microphone ? MicrophoneFormat : LoopbackFormat, device.Source);
            Opened.Add(source);
            return source;
        }
    }

    public SyntheticCaptureSource Latest(AudioSourceKind kind)
    {
        lock (sync)
            return Opened.Last(s => s.Kind == kind);
    }

    public int OpenCount { get { lock (sync) return Opened.Count; } }

    public void Unplug(AudioDevice device)
    {
        lock (sync)
            Devices.Remove(device);
        DeviceAvailabilityChanged?.Invoke(device.Id, false);
        DevicesChanged?.Invoke();
    }

    public void Replug(AudioDevice device)
    {
        lock (sync)
            Devices.Add(device);
        DeviceAvailabilityChanged?.Invoke(device.Id, true);
        DevicesChanged?.Invoke();
    }

    /// <summary>Dispositivo novo na lista (sem estar em uso).</summary>
    public void Add(AudioDevice device)
    {
        lock (sync)
            Devices.Add(device);
        DevicesChanged?.Invoke();
    }

    /// <summary>Só o padrão do Windows muda; nenhum dispositivo entra, sai ou muda de estado.</summary>
    public void ChangeDefault(AudioDevice newDefault)
    {
        lock (sync)
        {
            for (var i = 0; i < Devices.Count; i++)
            {
                if (Devices[i].Source == newDefault.Source)
                    Devices[i] = Devices[i] with { IsDefault = Devices[i].Id == newDefault.Id };
            }
        }
        DevicesChanged?.Invoke();
    }

    /// <summary>O fone some e outro dispositivo vira o padrão do Windows para a mesma fonte.</summary>
    public void Replace(AudioDevice removed, AudioDevice newDefault)
    {
        lock (sync)
        {
            Devices.Remove(removed);
            Devices.Add(newDefault);
        }
        DeviceAvailabilityChanged?.Invoke(removed.Id, false);
        DevicesChanged?.Invoke();
    }

    public void Dispose() { }
}

public sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>Nunca avança sozinho: os ticks vêm de <c>RecordingSession.Tick</c> no teste.</summary>
public sealed class IdleScheduler : IScheduler
{
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        await Task.Delay(Timeout.Infinite, cancellationToken);
}

public static class Pcm
{
    public static byte[] Float32Stereo(int rate, double frequency, double amplitude, int frames, long startFrame)
    {
        var bytes = new byte[frames * 2 * 4];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (startFrame + i) / rate));
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), value);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), value);
        }
        return bytes;
    }

    public static byte[] Int16Stereo(int rate, double frequency, double amplitude, int frames, long startFrame)
    {
        var bytes = new byte[frames * 2 * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequency * (startFrame + i) / rate));
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), value);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4 + 2), value);
        }
        return bytes;
    }

    /// <summary>Magnitude da componente de uma frequência (Goertzel), normalizada pela amplitude de um seno.</summary>
    public static double Tone(IReadOnlyList<short> samples, int start, int count, double frequency, int rate = 16000)
    {
        double s1 = 0, s2 = 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / rate);
        for (var i = 0; i < count; i++)
        {
            var s0 = samples[start + i] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / count;
    }

    public static short[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = new short[(bytes.Length - 44) / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(bytes, 44 + i * 2);
        return samples;
    }
}
