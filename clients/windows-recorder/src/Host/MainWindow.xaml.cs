using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Presentation.Recording;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host;

public partial class MainWindow : FluentWindow
{
    private readonly RecordingScreenViewModel screen;
    private readonly ShutdownGuard guard;
    private GlobalHotkey? hotkey;
    private readonly Func<Task> cleanup;
    private bool allowClose;

    public MainWindow(RecordingScreenViewModel screen, Func<Task> cleanup)
    {
        this.screen = screen;
        this.cleanup = cleanup;
        InitializeComponent();
        DataContext = screen;
        guard = new ShutdownGuard(screen, ConfirmStopAndSaveAsync);
        Loaded += async (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            await screen.RefreshDevicesAsync();
        };
    }

    /// <summary>O atalho global em uso, ou nulo se o Windows recusou todos os candidatos.</summary>
    public Hotkey? Hotkey => hotkey?.Registered;

    public TaskbarIndicator CreateTaskbarIndicator() => new(TaskbarItemInfo);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this);
        hotkey = new GlobalHotkey(new Win32HotkeyRegistrar(source.Handle));
        hotkey.Pressed += () => _ = screen.Toggle.ExecuteAsync();
        source.AddHook(WndProc);
        HotkeyHint.Text = hotkey.Registered is { } registered
            ? $"Atalho global para iniciar e parar, mesmo em segundo plano: {registered.DisplayName}"
            : "Não foi possível reservar um atalho global: outro programa usa as combinações. Use os botões ou o ícone da bandeja.";
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (hotkey?.HandleMessage(message, wParam) == true)
            handled = true;
        return 0;
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Sair pelo menu da bandeja: passa pela mesma confirmação de fechar a janela.</summary>
    public void RequestExit() => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (allowClose)
            return;
        e.Cancel = true;
        _ = CloseAfterGuardAsync();
    }

    private async Task CloseAfterGuardAsync()
    {
        if (!await guard.CanCloseAsync())
            return;
        allowClose = true;
        // Fechar de dentro de OnClosing lança; adia o Close para depois do ciclo atual da janela.
        await Task.Yield();
        hotkey?.Dispose();
        // Libera captura e bandeja antes de fechar, sem bloquear a interface.
        await cleanup();
        Close();
    }

    private async Task<bool> ConfirmStopAndSaveAsync()
    {
        ShowAndActivate();
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Gravação em andamento",
            Content = "Ainda há uma gravação em andamento. Deseja parar e salvar a gravação antes de fechar? " +
                "A gravação nunca é descartada sem a sua confirmação.",
            PrimaryButtonText = "Parar e salvar",
            CloseButtonText = "Continuar gravando",
            Owner = this
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
