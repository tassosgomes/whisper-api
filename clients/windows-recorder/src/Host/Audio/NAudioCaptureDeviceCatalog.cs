using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Audio;

/// <summary>Catálogo real: dispositivos ativos do Windows e notificações de mudança de estado (MMDeviceEnumerator).</summary>
public sealed class NAudioCaptureDeviceCatalog : ICaptureDeviceCatalog
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDeviceNotificationClient notifications;

    public NAudioCaptureDeviceCatalog()
    {
        // Sem contexto de sincronização: os eventos chegam na thread do sistema de áudio e só repassam o fato.
        notifications = enumerator.CreateNotificationClient(false);
        notifications.DeviceStateChanged += (_, e) => Raise(e.DeviceId, e.NewState == DeviceState.Active);
        notifications.DeviceRemoved += (_, e) => Raise(e.DeviceId, false);
    }

    public event Action<string, bool>? DeviceAvailabilityChanged;

    private void Raise(string? id, bool available)
    {
        if (id is not null)
            DeviceAvailabilityChanged?.Invoke(id, available);
    }

    private static DataFlow Flow(AudioSourceKind source) =>
        source == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;

    public IReadOnlyList<AudioDevice> List(AudioSourceKind source)
    {
        var defaultId = GetDefault(source)?.Id;
        using var devices = enumerator.EnumerateAudioEndPoints(Flow(source), DeviceState.Active);
        var result = new List<AudioDevice>();
        foreach (var device in devices)
        {
            using (device)
                result.Add(new AudioDevice(device.ID, device.FriendlyName, source, device.ID == defaultId));
        }
        return [.. result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public AudioDevice? GetDefault(AudioSourceKind source)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(Flow(source), Role.Console, out var device))
            return null;
        using (device)
            return new AudioDevice(device.ID, device.FriendlyName, source, true);
    }

    public ICaptureSource Open(AudioDevice device)
    {
        var endpoint = enumerator.GetDevice(device.Id);
        try
        {
            // NAudio 3 marca WasapiCapture como obsoleto em favor de WasapiRecorder; a API antiga segue
            // suportada e é a que a ADR-005 e a task 6.0 especificam. A troca fica restrita a este ponto.
#pragma warning disable CS0618
            IWaveIn capture = device.Source == AudioSourceKind.Microphone
                ? new WasapiCapture(endpoint)
                : new WasapiLoopbackCapture(endpoint);
#pragma warning restore CS0618
            return new NAudioCaptureSource(capture, endpoint);
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        notifications.Dispose();
        enumerator.Dispose();
    }
}

internal sealed class NAudioCaptureSource : ICaptureSource
{
    private readonly IWaveIn capture;
    private readonly MMDevice device;

    public NAudioCaptureSource(IWaveIn capture, MMDevice device)
    {
        this.capture = capture;
        this.device = device;
        var format = capture.WaveFormat;
        var standard = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;
        Format = new CaptureFormat(format.SampleRate, format.Channels, format.BitsPerSample,
            standard.Encoding == WaveFormatEncoding.IeeeFloat);
        capture.DataAvailable += (_, e) => DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public CaptureFormat Format { get; }
    public event Action<byte[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    public void Start() => capture.StartRecording();
    public void Stop() => capture.StopRecording();

    public void Dispose()
    {
        capture.Dispose();
        device.Dispose();
    }
}
