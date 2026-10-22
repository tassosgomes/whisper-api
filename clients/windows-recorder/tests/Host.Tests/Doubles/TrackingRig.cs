using System.Net;
using System.Text;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Tracking;
using Whisper.Recorder.Presentation.Uploading;
using Xunit;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>
/// Monta o acompanhamento (V-07) como o <c>App.Compose</c> faz, com portas em dublê: API em memória, conectividade,
/// notificador e agendador controlados, e uma thread de "interface" própria. Nada toca a rede nem a pasta real do app.
/// </summary>
public sealed class TrackingRig : IAsyncDisposable
{
    public const string Key = "wsk_00000000000000000000000000000000.segredo-de-teste";
    public const string CompletedResult =
        "{\"schemaVersion\":1,\"segments\":[{\"startMs\":1240,\"endMs\":2000,\"text\":\"Olá reunião\"}]}";

    private readonly bool keepDirectory;
    private readonly SyntheticCatalog catalog = new();
    private readonly WasapiAudioCapture capture;
    private readonly RecordingSession session;
    private readonly RecordingViewModel recording;
    private readonly ApiAccessProbe probe;
    private readonly UploadTransport uploadTransport = new();
    private readonly TrackingTransport trackingTransport;
    private readonly ApiAccessViewModel access;
    private readonly UploadViewModel upload;
    private int windowShown;

    private TrackingRig(string directory, bool keepDirectory, DateTimeOffset now, UiThread ui)
    {
        Directory = directory;
        this.keepDirectory = keepDirectory;
        Ui = ui;
        Clock.UtcNow = now;
        System.IO.Directory.CreateDirectory(directory);
        Repository = new RecordingRepository(Path.Combine(directory, "recordings"));
        Configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
        Configuration.Save(new Uri("https://api.example.test"));
        Vault.SaveAsync(Key).AsTask().GetAwaiter().GetResult();
        trackingTransport = new TrackingTransport(Api);
        probe = new ApiAccessProbe(Api);
        capture = new WasapiAudioCapture(catalog, Clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
        session = new RecordingSession(capture, Clock, new IdleScheduler(), Repository);
        // Os ViewModels capturam o contexto de quem os cria: aqui, a thread de "interface".
        (recording, Screen, access, upload, Flow) = ui.InvokeAsync(() =>
        {
            var recordingViewModel = new RecordingViewModel(session, Repository);
            var screen = new RecordingScreenViewModel(recordingViewModel, capture);
            var accessViewModel = new ApiAccessViewModel(new ApiAccessService(Vault, Configuration, probe,
                new DiagnosticLog(new StringWriter(), Clock)));
            var uploadViewModel = new UploadViewModel(new UploadCoordinator(Repository, Configuration, Vault, uploadTransport, Clock,
                new DiagnosticLog(new StringWriter(), Clock)));
            return (recordingViewModel, screen, accessViewModel, uploadViewModel,
                new UploadScreenViewModel(uploadViewModel, accessViewModel, screen));
        }).GetAwaiter().GetResult();
        Tracker = new JobTracker(Repository, Configuration, Vault, trackingTransport, uploadTransport, Connectivity, Clock, Scheduler, Toast);
        Tracking = new TrackingViewModel(Tracker, Screen, Scheduler);
        Activation = new NotificationActivation(Flow, () => Interlocked.Increment(ref windowShown));
    }

    public string Directory { get; }
    public UiThread Ui { get; }
    public TestClock Clock { get; } = new();
    public RecordingRepository Repository { get; }
    public ApiConfigurationStore Configuration { get; }
    public MemoryVault Vault { get; } = new();
    public FakeTrackingApi Api { get; } = new();
    public ControlledConnectivity Connectivity { get; } = new();
    public ControlledScheduler Scheduler { get; } = new();
    public MinimizedToast Toast { get; } = new();
    public RecordingScreenViewModel Screen { get; }
    public UploadScreenViewModel Flow { get; }
    public JobTracker Tracker { get; }
    public TrackingViewModel Tracking { get; }
    public NotificationActivation Activation { get; }
    public int WindowShown => Volatile.Read(ref windowShown);

    public static async Task<TrackingRig> CreateAsync(string? directory = null, bool keepDirectory = false, DateTimeOffset? now = null)
    {
        var ui = new UiThread();
        await Task.Yield();
        return new TrackingRig(directory ?? Path.Combine(Path.GetTempPath(), "whisper-host-v07-" + Guid.NewGuid().ToString("N")),
            keepDirectory, now ?? new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), ui);
    }

    /// <summary>Grava no disco uma gravação já enviada (em processamento) ou em outro estado.</summary>
    public Guid Seed(RecordingState state, string? jobId = null, string title = "Reunião")
    {
        var id = Guid.NewGuid();
        System.IO.Directory.CreateDirectory(Repository.DirectoryFor(id));
        Repository.Save(new RecordingMetadata(id, title, Clock.UtcNow, state, TimeSpan.FromMinutes(1),
            [AudioSourceKind.Microphone], [], UploadAttempt: 1, UploadId: jobId is null ? null : "upl_" + jobId,
            JobId: jobId, PutCompleted: jobId is not null));
        return id;
    }

    /// <summary>Lista como a interface a vê (lida na própria thread da interface).</summary>
    public Task<IReadOnlyList<RecordingListItem>> ListAsync() => Ui.InvokeAsync(() => Screen.Recordings);

    public async Task<RecordingListItem> ItemAsync(Guid id) => (await ListAsync()).Single(r => r.Id == id);

    /// <summary>Roda uma passagem do acompanhamento (a primeira ocorre ao iniciar) e espera ela terminar.</summary>
    public async Task PassAsync()
    {
        var before = Scheduler.Waiting;
        Scheduler.Tick();
        await Eventually(() => Scheduler.Waiting > before);
    }

    public async Task StartAsync()
    {
        Tracking.Start();
        await Eventually(() => Scheduler.Waiting >= 1);
    }

    public static async Task Eventually(Func<bool> condition, int seconds = 10)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("A condição esperada não aconteceu a tempo.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async Task WaitForCountAsync(int count)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while ((await ListAsync()).Count != count)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("A lista não chegou ao tamanho esperado.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async Task WaitForOfflineAsync(Guid id)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!(await ItemAsync(id)).IsOffline)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("A lista não mostrou o aviso de falta de conexão.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async Task WaitForDetailAsync(Guid id, string detail)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while ((await ItemAsync(id)).Detail != detail)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException("A lista não mostrou a mensagem esperada.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Espera a lista (já atualizada pela interface) mostrar a situação pedida para a gravação.</summary>
    public async Task<RecordingListItem> WaitForStatusAsync(Guid id, string status)
    {
        RecordingListItem? item = null;
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (item?.Status != status)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException($"A lista não mostrou \"{status}\" a tempo.");
            item = (await ListAsync()).SingleOrDefault(r => r.Id == id);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return item;
    }

    public async ValueTask DisposeAsync()
    {
        await Tracking.DisposeAsync();
        await Ui.InvokeAsync(() =>
        {
            Flow.Dispose();
            upload.Dispose();
            access.Dispose();
        });
        trackingTransport.Dispose();
        uploadTransport.Dispose();
        probe.Dispose();
        await Ui.InvokeAsync(async () =>
        {
            await Screen.DisposeAsync();
            recording.Dispose();
        });
        await session.DisposeAsync();
        await capture.DisposeAsync();
        Ui.Dispose();
        if (!keepDirectory)
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { }
        }
    }
}

public sealed class MemoryVault : ICredentialVault
{
    private string? value;

    public ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        value = apiKey;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(value);

    public ValueTask RemoveAsync(CancellationToken cancellationToken = default)
    {
        value = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class ControlledConnectivity : IConnectivity
{
    private volatile bool connected = true;
    public bool IsConnected { get => connected; set => connected = value; }
}

/// <summary>
/// O Windows com o app minimizado: o aviso aparece sem a janela, e o dublê guarda só o tipo e a gravação, nunca texto
/// da reunião. Fica claro no teste que a notificação não depende de a janela estar aberta.
/// </summary>
public sealed class MinimizedToast : INotifier
{
    private readonly object sync = new();
    private readonly List<(RecorderNotification Kind, Guid? Recording)> shown = [];
    public IReadOnlyList<(RecorderNotification Kind, Guid? Recording)> Shown { get { lock (sync) return shown.ToArray(); } }

    public ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default, Guid? recordingId = null)
    {
        lock (sync)
            shown.Add((notification, recordingId));
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Cada espera de 2 segundos do acompanhamento só termina quando o teste manda; a pausa de reinício espera o Wake.</summary>
public sealed class ControlledScheduler : IScheduler
{
    private readonly SemaphoreSlim ticks = new(0);
    private int waiting;
    private int faultNext;
    public int Waiting => Volatile.Read(ref waiting);
    public int RestartWaits { get; private set; }

    /// <summary>A próxima espera do acompanhamento falha com um erro inesperado.</summary>
    public void FaultNext() => Volatile.Write(ref faultNext, 1);
    public void Tick() => ticks.Release();

    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay == TrackingViewModel.RestartDelay)
        {
            RestartWaits++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        Interlocked.Increment(ref waiting);
        if (Interlocked.Exchange(ref faultNext, 0) == 1)
            throw new InvalidOperationException("falha simulada");
        await ticks.WaitAsync(cancellationToken);
    }
}

/// <summary>Rotas de status e resultado do contrato, em memória.</summary>
public sealed class FakeTrackingApi : HttpMessageHandler
{
    private readonly object sync = new();
    private readonly Dictionary<string, (string Status, string? Failure, bool Expired)> jobs = [];
    private int requests;
    public bool NetworkDown { get; set; }
    public int Requests => Volatile.Read(ref requests);

    public void Job(string jobId, string status, string? failureCode = null)
    {
        lock (sync)
            jobs[jobId] = (status, failureCode, false);
    }

    public void Expire(string jobId)
    {
        lock (sync)
            jobs[jobId] = ("completed", null, true);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        if (NetworkDown)
            throw new HttpRequestException("sem conexão");
        var path = request.RequestUri!.AbsolutePath;
        var isResult = path.EndsWith("/result", StringComparison.Ordinal);
        var jobId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[isResult ? ^2 : ^1];
        (string Status, string? Failure, bool Expired) job;
        lock (sync)
        {
            if (!jobs.TryGetValue(jobId, out job))
                return Task.FromResult(Problem(HttpStatusCode.NotFound));
        }
        if (job.Expired)
            return Task.FromResult(Problem(HttpStatusCode.NotFound));
        if (isResult)
            return Task.FromResult(Json(TrackingRig.CompletedResult));
        var failure = job.Failure is null ? "" : $",\"failure\":{{\"code\":\"{job.Failure}\",\"message\":\"x\"}}";
        return Task.FromResult(Json($"{{\"jobId\":\"{jobId}\",\"status\":\"{job.Status}\",\"createdAt\":\"2026-10-21T12:00:00Z\"," +
            $"\"updatedAt\":\"2026-10-21T12:00:05Z\"{failure}}}"));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(
                $"{{\"type\":\"about:blank\",\"title\":\"Erro\",\"status\":{(int)status},\"code\":\"NOT_FOUND\"}}",
                Encoding.UTF8, "application/problem+json")
        };
}
