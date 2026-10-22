using System.Diagnostics;
using System.Net;
using System.Text;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Uploading;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Enviar uma gravação enquanto outra está sendo gravada: a thread da interface continua respondendo
/// (confirmação, leitura de metadados e hash do arquivo rodam fora dela). A "interface" é uma thread com fila.
/// </summary>
public sealed class V06ResponsivenessTests : IAsyncDisposable
{
    private const string Key = "wsk_00000000000000000000000000000000.segredo-de-teste";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-v06-ui-" + Guid.NewGuid().ToString("N"));
    private readonly UiThread ui = new();
    private readonly SyntheticCatalog catalog = new();
    private readonly TestClock clock = new();
    private readonly Handler handler = new();
    private WasapiAudioCapture capture = null!;
    private RecordingSession session = null!;
    private RecordingViewModel recording = null!;
    private RecordingScreenViewModel screen = null!;
    private ApiAccessProbe probe = null!;
    private UploadTransport transport = null!;
    private ApiAccessViewModel access = null!;
    private UploadViewModel upload = null!;
    private UploadScreenViewModel flow = null!;
    private RecordingRepository repository = null!;

    public async ValueTask DisposeAsync()
    {
        await ui.InvokeAsync(async () =>
        {
            flow.Dispose();
            upload.Dispose();
            access.Dispose();
            await screen.DisposeAsync();
            recording.Dispose();
            await session.DisposeAsync();
            await capture.DisposeAsync();
        });
        transport.Dispose();
        probe.Dispose();
        ui.Dispose();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private void EmitBlocks(int count, long first)
    {
        for (var i = 0; i < count; i++)
        {
            catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.5, 480, (first + i) * 480L));
            catalog.Latest(AudioSourceKind.SystemLoopback).Emit(Pcm.Int16Stereo(44100, 330, 0.5, 441, (first + i) * 441L));
            clock.UtcNow += TimeSpan.FromMilliseconds(10);
            if (i % 10 == 9)
                session.Tick();
        }
    }

    [Fact]
    public async Task SendingAnotherRecordingWhileRecordingNeverBlocksTheUiThread()
    {
        var vault = new MemoryVault();
        var configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
        configuration.Save(new Uri("https://api.example.test"));
        await vault.SaveAsync(Key, Token);
        await ui.InvokeAsync(() =>
        {
            capture = new WasapiAudioCapture(catalog, clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
            repository = new RecordingRepository(Path.Combine(directory, "recordings"));
            session = new RecordingSession(capture, clock, new IdleScheduler(), repository);
            recording = new RecordingViewModel(session, repository);
            screen = new RecordingScreenViewModel(recording, capture);
            probe = new ApiAccessProbe(handler);
            transport = new UploadTransport(handler, handler);
            access = new ApiAccessViewModel(new ApiAccessService(vault, configuration, probe));
            upload = new UploadViewModel(new UploadCoordinator(repository, configuration, vault, transport, clock));
            flow = new UploadScreenViewModel(upload, access, screen);
        });

        // Primeira gravação, para ser enviada: 200 MB de arquivo.
        await ui.InvokeAsync(() => screen.RefreshDevicesAsync());
        await ui.InvokeAsync(() => screen.StartAsync());
        EmitBlocks(50, 0);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        await ui.InvokeAsync(() => screen.StopAsync());
        await Task.Delay(300, Token); // a interface reabre a pré-visualização dos dispositivos
        var toSend = Assert.Single(repository.List());
        using (var file = new FileStream(repository.AudioPath(toSend.Id), FileMode.Open, FileAccess.Write))
            file.SetLength(200L * 1024 * 1024);
        await ui.InvokeAsync(() => screen.RefreshDevicesAsync());

        // Segunda gravação em andamento, com a captura entregando blocos e níveis sem parar.
        await ui.InvokeAsync(() => screen.StartAsync());
        Assert.True(await ui.InvokeAsync(() => screen.IsRecording));
        using var stop = new CancellationTokenSource();
        var emitter = Task.Run(() =>
        {
            long block = 1000;
            while (!stop.IsCancellationRequested)
            {
                EmitBlocks(10, block);
                block += 10;
                Thread.Sleep(100);
            }
        }, Token);

        // Batimento: mede quanto cada tarefa postada espera até a thread da interface executá-la.
        var worst = 0L;
        var beats = 0;
        var heartbeat = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var posted = Stopwatch.GetTimestamp();
                ui.Post(() =>
                {
                    var wait = (long)Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    Interlocked.Increment(ref beats);
                    if (wait > Interlocked.Read(ref worst))
                        Interlocked.Exchange(ref worst, wait);
                });
                await Task.Delay(10);
            }
        }, Token);

        await ui.InvokeAsync(async () =>
        {
            screen.RefreshRecordings();
            flow.Select(screen.Recordings.Single(r => r.Id == toSend.Id));
            await flow.Transcribe.ExecuteAsync();
            Assert.Equal(UploadPhase.Confirming, flow.Phase);
            await flow.Confirm.ExecuteAsync();
            Assert.Equal(UploadPhase.Accepted, flow.Phase);
        });
        await stop.CancelAsync();
        await Task.WhenAll(emitter, heartbeat);

        Assert.Equal(200L * 1024 * 1024, handler.PutBytes);
        Assert.True(await ui.InvokeAsync(() => screen.IsRecording)); // a gravação em andamento continua
        Assert.InRange(beats, 20, int.MaxValue);
        Assert.True(Interlocked.Read(ref worst) < 400, $"A interface ficou {worst} ms sem responder.");
        await ui.InvokeAsync(() => screen.StopAsync());
    }

    private sealed class MemoryVault : ICredentialVault
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

    /// <summary>API e armazenamento mínimos em memória.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public long PutBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"x\",\"status\":404,\"code\":\"NOT_FOUND\"}");
            if (request.Method == HttpMethod.Put)
            {
                var sink = new Counter();
                await request.Content!.CopyToAsync(sink, cancellationToken);
                PutBytes = sink.Total;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path.EndsWith("/complete", StringComparison.Ordinal))
                return Json(HttpStatusCode.Accepted, "{\"jobId\":\"trn_1\",\"status\":\"queued\",\"createdAt\":\"2026-10-21T12:00:00Z\",\"statusUrl\":\"/v1/transcriptions/trn_1\"}");
            return Json(HttpStatusCode.Created,
                "{\"uploadId\":\"upl_1\",\"expiresAt\":\"2026-10-21T15:00:00Z\",\"upload\":{\"method\":\"PUT\"," +
                "\"url\":\"https://storage.example.test/whisper/uploads/upl_1?signature=x\",\"headers\":{\"Content-Type\":\"audio/wav\"}}," +
                "\"completeUrl\":\"/v1/transcription-uploads/upl_1/complete\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.NotFound ? "application/problem+json" : "application/json") };
    }

    private sealed class Counter : Stream
    {
        public long Total { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Total += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Total += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
