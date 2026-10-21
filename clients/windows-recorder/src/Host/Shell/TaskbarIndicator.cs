using System.Windows.Shell;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Indicador na barra de tarefas: ícone sobreposto (anel, círculo, barras), barra de progresso fixa
/// (vermelha gravando, amarela pausada) e descrição com o tempo, lida por leitores de tela.
/// </summary>
public sealed class TaskbarIndicator(TaskbarItemInfo info) : IRecordingIndicatorSurface
{
    public void Apply(RecordingIndicatorState state)
    {
        switch (state.Kind)
        {
            case RecordingIndicatorKind.Recording:
                info.ProgressState = TaskbarItemProgressState.Error;
                info.ProgressValue = 1;
                info.Overlay = RecorderIcons.Render(state.Kind, 16);
                info.Description = $"Gravando {state.Elapsed}";
                break;
            case RecordingIndicatorKind.Paused:
                info.ProgressState = TaskbarItemProgressState.Paused;
                info.ProgressValue = 1;
                info.Overlay = RecorderIcons.Render(state.Kind, 16);
                info.Description = $"Gravação pausada {state.Elapsed}";
                break;
            default:
                info.ProgressState = TaskbarItemProgressState.None;
                info.ProgressValue = 0;
                info.Overlay = null;
                info.Description = string.Empty;
                break;
        }
    }
}
