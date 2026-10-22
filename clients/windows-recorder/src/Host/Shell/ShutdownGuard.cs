using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Fechar durante a gravação sempre pergunta. Só "parar e salvar" fecha, e salva antes; qualquer outra
/// resposta continua gravando. Nunca descarta a gravação.
/// </summary>
public sealed class ShutdownGuard(RecordingScreenViewModel screen, Func<Task<bool>> confirmStopAndSave)
{
    public async Task<bool> CanCloseAsync()
    {
        if (!screen.IsRecording)
            return true;
        if (!await confirmStopAndSave())
            return false;
        await screen.StopAsync();
        return true;
    }
}
