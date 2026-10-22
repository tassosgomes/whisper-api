namespace Whisper.Recorder.Core.Ports;

/// <summary>
/// Extensão opcional da porta de captura: avisa quando a lista de dispositivos muda (entrada, saída,
/// mudança de estado ou do padrão) e diz qual dispositivo cada fonte realmente usa, que pode diferir do
/// escolhido quando o host reabriu a fonte no novo padrão do sistema.
/// </summary>
public interface IAudioDeviceChanges
{
    /// <summary>Pode ser disparado em qualquer thread.</summary>
    event Action? DevicesChanged;

    /// <summary>O dispositivo em uso pela fonte, ou nulo se ela não está aberta ou está perdida.</summary>
    AudioDevice? ActiveDevice(AudioSourceKind source);
}
