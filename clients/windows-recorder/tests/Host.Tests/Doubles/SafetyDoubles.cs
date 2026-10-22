using System.Net;
using System.Text;
using NAudio.Wave;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>
/// Saída de mentira que lê o áudio em outra thread, como o <c>WaveOut</c> real, sem tocar nada. Serve para provar que
/// buscas feitas pela interface durante a leitura não corrompem a posição nem derrubam a leitura.
/// </summary>
public sealed class ThreadedWavePlayer : IWavePlayer
{
    private readonly object sync = new();
    private IWaveProvider? provider;
    private Thread? thread;
    private volatile bool running;
    private volatile PlaybackState state = PlaybackState.Stopped;
    private long reads;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState => state;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => provider?.WaveFormat ?? new WaveFormat(16000, 16, 1);
    public long Reads => Interlocked.Read(ref reads);
    public Exception? Failure { get; private set; }

    public void Init(IWaveProvider waveProvider) => provider = waveProvider;

    public void Play()
    {
        state = PlaybackState.Playing;
        lock (sync)
        {
            if (thread is not null)
                return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "Teste: thread da saída" };
            thread.Start();
        }
    }

    public void Pause() => state = PlaybackState.Paused;

    public void Stop()
    {
        state = PlaybackState.Stopped;
        Thread? current;
        lock (sync)
        {
            running = false;
            current = thread;
            thread = null;
        }
        current?.Join();
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        var buffer = new byte[3200];
        try
        {
            while (running)
            {
                if (state != PlaybackState.Playing)
                {
                    Thread.Sleep(1);
                    continue;
                }
                var read = provider!.Read(buffer);
                if (read % 2 != 0)
                    throw new InvalidOperationException("Leitura desalinhada.");
                Interlocked.Increment(ref reads);
                if (read == 0)
                    Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
            state = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }
}

/// <summary>
/// API e armazenamento em memória só para o envio: o PUT espera o teste liberar, deixando um envio "em andamento".
/// </summary>
public sealed class GatedUploadService : HttpMessageHandler
{
    public TaskCompletionSource Halfway { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get)
            return Problem(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Put)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            Halfway.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        if (path.EndsWith("/complete", StringComparison.Ordinal))
            return Json(HttpStatusCode.Accepted,
                "{\"jobId\":\"trn_1\",\"status\":\"queued\",\"createdAt\":\"2026-10-21T12:00:00Z\",\"statusUrl\":\"/v1/transcriptions/trn_1\"}");
        return Json(HttpStatusCode.Created,
            "{\"uploadId\":\"upl_1\",\"expiresAt\":\"2026-10-21T15:00:00Z\",\"upload\":{\"method\":\"PUT\"," +
            "\"url\":\"https://storage.example.test/whisper/uploads/upl_1?signature=x\"," +
            "\"headers\":{\"Content-Type\":\"audio/wav\"}},\"completeUrl\":\"/v1/transcription-uploads/upl_1/complete\"}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(
                $"{{\"type\":\"about:blank\",\"title\":\"Erro\",\"status\":{(int)status},\"code\":\"NOT_FOUND\"}}",
                Encoding.UTF8, "application/problem+json")
        };
}
