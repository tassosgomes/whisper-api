using System.IO;
using System.Text.Json;
using System.Windows;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Presentation.Recording;
using Wpf.Ui.Appearance;

namespace Whisper.Recorder.Host;

public partial class App : Application
{
    private TextWriter diagnosticWriter = TextWriter.Null;
    private DiagnosticLog? diagnostic;
    private Mutex? singleInstance;
    private WasapiAudioCapture? capture;
    private RecordingSession? session;
    private RecordingViewModel? recordingViewModel;
    private RecordingScreenViewModel? screen;
    private RecordingIndicatorCoordinator? coordinator;
    private TrayIndicator? tray;
    private MainWindow? window;
    private Task? cleanup;

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new Mutex(true, "Whisper.Recorder.Host", out var created);
        if (!created)
        {
            // Duas instâncias disputariam o atalho global e a pasta de gravações.
            Shutdown();
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Whisper", "Recorder");
            Directory.CreateDirectory(directory);
            diagnosticWriter = new StreamWriter(Path.Combine(directory, "diagnostics.log"), append: true);
            diagnostic = new DiagnosticLog(diagnosticWriter, new SystemClock());
            diagnostic.Write(DiagnosticEvent.ApplicationStarted);
        }
        catch (IOException)
        {
            // An unavailable diagnostic file must not prevent the app from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not log exception messages: they can contain local paths.
        }

        base.OnStartup(e);
        // O tema segue o do sistema (claro ou escuro) desde a primeira janela; o Mica continua no XAML.
        ApplicationThemeManager.ApplySystemTheme();
        Compose();
    }

    /// <summary>Monta o host: repositório, captura WASAPI, sessão do núcleo, tela e indicadores.</summary>
    private void Compose()
    {
        var clock = new SystemClock();
        var repository = new RecordingRepository();
        try
        {
            // Antes de criar a sessão: uma gravação que ficou em "recording" vira "interrompida".
            repository.RecoverInterrupted();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Metadados ilegíveis não impedem o app de abrir; as demais gravações continuam acessíveis.
        }

        capture = new WasapiAudioCapture(new NAudioCaptureDeviceCatalog(), clock, diagnostic: diagnostic);
        session = new RecordingSession(capture, clock, new SystemScheduler(), repository);
        recordingViewModel = new RecordingViewModel(session, repository);
        screen = new RecordingScreenViewModel(recordingViewModel, capture);

        window = new MainWindow(screen, CleanupAsync);
        SessionEnding += OnSessionEnding;
        window.Show();

        var hotkeyName = window.Hotkey?.DisplayName ?? "sem atalho";
        tray = new TrayIndicator(hotkeyName,
            open: () => window.ShowAndActivate(),
            toggle: () => _ = screen.Toggle.ExecuteAsync(),
            exit: () => window.RequestExit());
        coordinator = new RecordingIndicatorCoordinator(screen, tray, [window.CreateTaskbarIndicator(), tray]);
    }

    /// <summary>
    /// Logoff ou desligamento: salva a gravação em andamento sem diálogo. O Windows espera o handler
    /// terminar (poucos segundos), então aqui o bloqueio é intencional e limitado; roda fora do contexto da interface.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (screen is null)
            return;
        try
        {
            Task.Run(screen.StopForShutdownAsync).Wait(TimeSpan.FromSeconds(4));
        }
        catch (AggregateException)
        {
            // O que já foi gravado permanece; a recuperação marca como interrompida se algo faltar.
        }
    }

    /// <summary>Libera tray, captura e sessão uma única vez, sem bloquear a interface.</summary>
    private Task CleanupAsync()
    {
        if (cleanup is null)
        {
            // Na thread da interface: tray e coordenador pertencem a ela.
            coordinator?.Dispose();
            tray?.Dispose();
            cleanup = CleanupCoreAsync();
        }
        return cleanup;
    }

    private async Task CleanupCoreAsync()
    {
        try
        {
            if (screen is not null)
                await screen.DisposeAsync().ConfigureAwait(false);
            recordingViewModel?.Dispose();
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            if (capture is not null)
                await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Encerrar o app não pode falhar por causa da limpeza; a recuperação trata o que sobrar.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Caminho normal: a janela já chamou CleanupAsync antes de fechar. Só um encerramento que não passou
        // por ela (fim de sessão) chega aqui sem limpeza; como tudo termina de forma síncrona, o bloqueio é curto.
        if (cleanup is null)
            CleanupAsync().GetAwaiter().GetResult();

        try
        {
            diagnostic?.Write(DiagnosticEvent.ApplicationStopped);
        }
        catch (IOException)
        {
            // Diagnostics are best effort and never block shutdown.
        }
        try
        {
            diagnosticWriter.Dispose();
        }
        catch (IOException)
        {
            // A failed flush must not prevent shutdown either.
        }

        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
