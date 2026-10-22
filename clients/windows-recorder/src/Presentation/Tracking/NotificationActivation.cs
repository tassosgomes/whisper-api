using Whisper.Recorder.Presentation.Uploading;

namespace Whisper.Recorder.Presentation.Tracking;

/// <summary>
/// Ação ao tocar na notificação do Windows: abre/foca o app e seleciona a gravação a que ela se refere.
/// O host só liga o toque do usuário a <see cref="Activate"/> e informa como mostrar a janela.
/// </summary>
public sealed class NotificationActivation(UploadScreenViewModel screen, Action showWindow)
{
    public void Activate(Guid? recordingId)
    {
        showWindow();
        if (recordingId is { } id)
            screen.SelectRecording(id);
    }
}
