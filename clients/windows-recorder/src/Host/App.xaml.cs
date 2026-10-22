using System.IO;
using System.Text.Json;
using System.Windows;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Transcripts;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Host.Access;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Management;
using Whisper.Recorder.Host.Playback;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Host.Transcripts;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Playback;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Tracking;
using Whisper.Recorder.Presentation.Transcripts;
using Whisper.Recorder.Presentation.Uploading;
using Wpf.Ui.Appearance;

namespace Whisper.Recorder.Host;

public partial class App : Application
{
    private ReleasableLogWriter? diary;
    private string dataDirectory = "";
    private DiagnosticLog? diagnostic;
    private Mutex? singleInstance;
    private WasapiAudioCapture? capture;
    private RecordingSession? session;
    private RecordingViewModel? recordingViewModel;
    private RecordingScreenViewModel? screen;
    private ApiAccessProbe? accessProbe;
    private ApiAccessViewModel? accessViewModel;
    private UploadTransport? uploadTransport;
    private UploadViewModel? uploadViewModel;
    private UploadScreenViewModel? uploadScreen;
    private TranscriptScreenViewModel? transcriptScreen;
    private RecordingManagementScreenViewModel? management;
    private TrackingTransport? trackingTransport;
    private WindowsConnectivity? connectivity;
    private TrackingViewModel? tracking;
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
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Whisper", "Recorder");
            Directory.CreateDirectory(dataDirectory);
            // O diário abre sob demanda e pode ser solto: "apagar todos os dados" esvazia a pasta com o app aberto.
            diary = new ReleasableLogWriter(Path.Combine(dataDirectory, "diagnostics.log"));
            diagnostic = new DiagnosticLog(diary, new SystemClock());
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
        if (dataDirectory.Length == 0)
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Whisper", "Recorder");
        diary ??= new ReleasableLogWriter(Path.Combine(dataDirectory, "diagnostics.log"));
        var repository = new RecordingRepository();
        // Trava única das operações sobre os dados locais (apagar uma gravação, apagar tudo).
        var gate = new DataOperationGate();
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
        screen = new RecordingScreenViewModel(recordingViewModel, capture, gate);

        // Configuração do acesso à API: opcional para gravar, pedida só no primeiro envio.
        var vault = new WindowsCredentialVault();
        var configuration = new ApiConfigurationStore(ApiConfigurationStore.DefaultPath);
        accessProbe = new ApiAccessProbe();
        accessViewModel = new ApiAccessViewModel(new ApiAccessService(vault, configuration, accessProbe, diagnostic), gate);

        // Envio (10.0): HttpClient real com a validação de certificado do Windows, sem redirecionamento nem retry
        // automático. O coordenador é compartilhado com o repositório da sessão (exclusão mútua das operações).
        uploadTransport = new UploadTransport();
        uploadViewModel = new UploadViewModel(new UploadCoordinator(repository, configuration, vault, uploadTransport, clock, diagnostic));
        uploadScreen = new UploadScreenViewModel(uploadViewModel, accessViewModel, screen, gate);

        // Transcrição (14.0): lida só dos arquivos locais, com a área de transferência e os diálogos do Windows.
        transcriptScreen = new TranscriptScreenViewModel(new TranscriptViewModel(new LocalTranscriptStore(repository)),
            new WindowsClipboard(), new WindowsTranscriptDialogs(() => window?.ActiveTranscriptOwner));

        // Gestão (16.0): player sobre o NAudio, renomear, apagar e apagar tudo. O núcleo decide; o host libera janelas e diário.
        var player = new PlayerScreenViewModel(() => new NAudioAudioPlayer(), repository);
        management = new RecordingManagementScreenViewModel(
            new RecordingManagementViewModel(new RecordingManagement(repository, vault, dataDirectory)),
            screen, uploadScreen, accessViewModel, new WindowsManagementDialogs(ActiveWindow),
            new WindowsManagementHost(() => window, diary), gate, dataDirectory);

        window = new MainWindow(screen, accessViewModel, uploadScreen, transcriptScreen, management, player, CleanupAsync);
        // Sem acesso confirmado no primeiro envio, ou com a chave recusada (401), abre as configurações.
        accessViewModel.ConfigurationRequested += () => window.ShowSettings();
        uploadScreen.ConfigurationRequested += () => window.ShowSettings();
        SessionEnding += OnSessionEnding;
        window.Show();

        var hotkeyName = window.Hotkey?.DisplayName ?? "sem atalho";
        tray = new TrayIndicator(hotkeyName,
            open: () => window.ShowAndActivate(),
            toggle: () => _ = screen.Toggle.ExecuteAsync(),
            exit: () => window.RequestExit());
        coordinator = new RecordingIndicatorCoordinator(screen, tray, [window.CreateTaskbarIndicator(), tray]);

        // Acompanhamento (12.0): o tracker do núcleo decide quando notificar; o ícone da bandeja só exibe o aviso e,
        // ao ser tocado, abre/foca a janela e seleciona a gravação. Inicia agora e retoma o que estava em processamento.
        var activation = new NotificationActivation(uploadScreen, () => window.ShowAndActivate());
        tray.NotificationActivated += activation.Activate;
        trackingTransport = new TrackingTransport();
        connectivity = new WindowsConnectivity();
        var tracker = new JobTracker(repository, configuration, vault, trackingTransport, uploadTransport,
            connectivity, clock, new SystemScheduler(), tray);
        tracking = new TrackingViewModel(tracker, screen, new SystemScheduler());
        // Novo envio aceito: se o acompanhamento estiver esperando para recomeçar, recomeça agora.
        uploadViewModel.RecordingChanged += tracking.Wake;
        tracking.Start();
    }

    /// <summary>A janela ativa do app (principal, configurações...), dona dos diálogos de confirmação.</summary>
    private Window? ActiveWindow() => Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? window;

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
            // O acompanhamento para antes do ícone: um aviso tardio não pode usar a bandeja já liberada.
            tracking?.Stop();
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
            if (tracking is not null)
                await tracking.DisposeAsync().ConfigureAwait(false);
            connectivity?.Dispose();
            trackingTransport?.Dispose();
            if (screen is not null)
                await screen.DisposeAsync().ConfigureAwait(false);
            management?.Dispose();
            uploadScreen?.Dispose();
            uploadViewModel?.Dispose();
            uploadTransport?.Dispose();
            recordingViewModel?.Dispose();
            accessViewModel?.Dispose();
            accessProbe?.Dispose();
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
            diary?.Dispose();
        }
        catch (IOException)
        {
            // A failed flush must not prevent shutdown either.
        }

        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
