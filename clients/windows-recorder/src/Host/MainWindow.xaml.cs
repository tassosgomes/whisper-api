using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Whisper.Recorder.Host.Access;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Uploading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host;

public partial class MainWindow : FluentWindow
{
    private readonly RecordingScreenViewModel screen;
    private readonly ApiAccessViewModel access;
    private readonly UploadScreenViewModel upload;
    private ApiAccessWindow? settings;
    private readonly ShutdownGuard guard;
    private GlobalHotkey? hotkey;
    private readonly Func<Task> cleanup;
    private bool allowClose;

    public MainWindow(RecordingScreenViewModel screen, ApiAccessViewModel access, UploadScreenViewModel upload, Func<Task> cleanup)
    {
        this.screen = screen;
        this.access = access;
        this.upload = upload;
        this.cleanup = cleanup;
        InitializeComponent();
        DataContext = screen;
        UploadArea.DataContext = upload;
        // A lista é recriada a cada atualização de situação; a escolha do usuário volta para a mesma gravação.
        screen.PropertyChanged += (_, e) =>
        {
            // Só quando a lista pode ter sido recriada; cronômetro e níveis não passam por aqui.
            if (e.PropertyName is null or nameof(RecordingScreenViewModel.Recordings))
                Dispatcher.BeginInvoke(RestoreSelection, System.Windows.Threading.DispatcherPriority.Background);
        };
        // Toque na notificação: a lista mostra a gravação escolhida pelo código.
        upload.SelectionRequested += _ => Dispatcher.BeginInvoke(RestoreSelection, System.Windows.Threading.DispatcherPriority.Background);
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

    /// <summary>Abre as configurações do acesso à API (uma única janela); também é o destino do primeiro envio.</summary>
    public void ShowSettings()
    {
        if (settings is null)
        {
            settings = new ApiAccessWindow(access) { Owner = this };
            settings.Closed += (_, _) => settings = null;
        }
        settings.Show();
        settings.Activate();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnRecordingSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecordingList.SelectedItem is RecordingListItem item)
            upload.Select(item);
    }

    private void RestoreSelection()
    {
        if (upload.SelectedRecordingId is not { } id || (RecordingList.SelectedItem as RecordingListItem)?.Id == id)
            return;
        if (screen.Recordings.FirstOrDefault(r => r.Id == id) is { } item)
        {
            RecordingList.SelectedItem = item;
            RecordingList.ScrollIntoView(item);
        }
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
