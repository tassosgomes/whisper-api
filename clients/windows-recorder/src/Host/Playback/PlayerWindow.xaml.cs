using System.Windows.Input;
using System.Windows.Threading;
using Whisper.Recorder.Presentation.Playback;
using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host.Playback;

/// <summary>
/// Janela do player. Só liga a tela ao <see cref="PlayerScreenViewModel"/>: os atalhos chamam os mesmos comandos dos botões
/// e um temporizador (4 vezes por segundo, só enquanto a janela existe) pede a posição ao player. Ao fechar, o arquivo de
/// áudio é liberado; o host também chama a liberação antes de apagar a gravação.
/// </summary>
public partial class PlayerWindow : FluentWindow
{
    private readonly PlayerScreenViewModel screen;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public PlayerWindow(PlayerScreenViewModel screen)
    {
        this.screen = screen;
        InitializeComponent();
        DataContext = screen;
        // Com o foco no controle deslizante ou num botão, as setas e o Espaço seguem o comportamento do próprio controle.
        InputBindings.Add(new KeyBinding(screen.PlayPause, Key.Space, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(screen.Back, Key.Left, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(screen.Forward, Key.Right, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(screen.Close, Key.Escape, ModifierKeys.None));
        screen.CloseRequested += Close;
        timer.Tick += (_, _) => screen.Tick();
        timer.Start();
        Loaded += (_, _) => PlayButton.Focus();
        Closed += (_, _) =>
        {
            timer.Stop();
            screen.CloseRequested -= Close;
            // Solta o WAV: apagar a gravação logo depois depende disso.
            _ = screen.ReleaseAsync();
        };
    }
}
