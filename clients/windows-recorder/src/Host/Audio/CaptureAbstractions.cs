using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Audio;

/// <summary>Formato PCM nativo da fonte, como o WASAPI o entrega no modo compartilhado.</summary>
public sealed record CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>
/// Uma fonte WASAPI aberta (microfone ou loopback). Existe para o host trocar o NAudio por
/// uma fonte sintética nos testes. O buffer recebido é reaproveitado pelo emissor: copie antes de retornar.
/// </summary>
public interface ICaptureSource : IDisposable
{
    CaptureFormat Format { get; }
    event Action<byte[], int>? DataAvailable;
    /// <summary>Parada da captura; a exceção, quando houver, indica que o dispositivo falhou.</summary>
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
}

/// <summary>Enumera dispositivos, abre fontes e avisa quando o estado de um dispositivo muda.</summary>
public interface ICaptureDeviceCatalog : IDisposable
{
    /// <summary>Dispositivos ativos da fonte: entrada para o microfone, saída (loopback) para o sistema.</summary>
    IReadOnlyList<AudioDevice> List(AudioSourceKind source);
    AudioDevice? GetDefault(AudioSourceKind source);
    /// <summary>Abre a fonte; lança <see cref="UnauthorizedAccessException"/> quando o Windows nega o acesso.</summary>
    ICaptureSource Open(AudioDevice device);
    /// <summary>Id do dispositivo e se ele está ativo. Pode chegar em qualquer thread.</summary>
    event Action<string, bool>? DeviceAvailabilityChanged;
    /// <summary>Entrada, saída, mudança de estado ou do dispositivo padrão. Pode chegar em qualquer thread.</summary>
    event Action? DevicesChanged;
}
