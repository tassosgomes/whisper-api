using System.ComponentModel;
using System.Windows.Input;
using Whisper.Recorder.Presentation.Transcripts;
using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host.Transcripts;

/// <summary>
/// Tela de leitura da transcrição. Só liga a janela ao <see cref="TranscriptScreenViewModel"/>: os atalhos chamam os mesmos
/// comandos dos botões, então desligar um comando desliga o atalho. Não consulta o serviço.
/// </summary>
public partial class TranscriptWindow : FluentWindow
{
    private readonly TranscriptScreenViewModel screen;
    private bool focusPlaced;

    public TranscriptWindow(TranscriptScreenViewModel screen)
    {
        this.screen = screen;
        InitializeComponent();
        DataContext = screen;
        InputBindings.Add(new KeyBinding(screen.CopyWithTimes, Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(screen.CopyWithoutTimes, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(screen.Export, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(screen.Close, Key.Escape, ModifierKeys.None));
        screen.CloseRequested += Close;
        screen.PropertyChanged += OnScreenChanged;
        Closed += (_, _) =>
        {
            screen.CloseRequested -= Close;
            screen.PropertyChanged -= OnScreenChanged;
        };
    }

    /// <summary>Quando a leitura termina, o foco vai para a lista de trechos (ou para o botão Fechar, sem trechos).</summary>
    private void OnScreenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (screen.IsLoading)
        {
            focusPlaced = false;
            return;
        }
        if (focusPlaced)
            return;
        focusPlaced = true;
        Dispatcher.BeginInvoke(() =>
        {
            // Outra leitura começou antes deste momento: o foco será colocado quando ela terminar.
            if (screen.IsLoading)
            {
                focusPlaced = false;
                return;
            }
            if (screen.ShowsLines)
            {
                SegmentList.SelectedIndex = 0;
                SegmentList.UpdateLayout();
                (SegmentList.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.UIElement ?? SegmentList).Focus();
            }
            else
                CloseButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
