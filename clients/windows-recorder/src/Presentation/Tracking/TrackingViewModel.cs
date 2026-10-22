using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Presentation.Tracking;

/// <summary>
/// Liga o <see cref="JobTracker"/> à tela: roda o acompanhamento em segundo plano (fora da thread da interface) e
/// encaminha cada mudança de situação para a lista, que coalesce os avisos e troca só a linha afetada. O núcleo decide
/// o que acompanhar e quando notificar; aqui só há ciclo de vida. Se o acompanhamento parar por um erro inesperado,
/// recomeça depois de uma pausa (ou assim que um novo envio for aceito, via <see cref="Wake"/>).
/// </summary>
public sealed class TrackingViewModel(JobTracker tracker, RecordingScreenViewModel screen, IScheduler scheduler) : IAsyncDisposable
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? run;
    private int started;

    /// <summary>Quantas vezes o acompanhamento recomeçou depois de um erro inesperado.</summary>
    public int Restarts { get; private set; }

    /// <summary>Inicia na abertura do app; retoma o que estava em <c>uploading</c>/<c>processing</c> e segue os novos envios.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
            return;
        tracker.Changed += screen.ApplyTracking;
        run = Task.Run(() => SuperviseAsync(cancellation.Token));
    }

    /// <summary>Um envio foi aceito: se o acompanhamento estiver esperando para recomeçar, recomeça agora.</summary>
    public void Wake()
    {
        // No máximo uma permissão pendente: avisos repetidos durante o ciclo normal não se acumulam.
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Pede o encerramento sem esperar; usado antes de liberar o resto do host.</summary>
    public void Stop()
    {
        tracker.Changed -= screen.ApplyTracking;
        cancellation.Cancel();
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Uma permissão sobrando de antes não pode fazer o próximo reinício pular a pausa.
            while (wake.Wait(0)) { }
            try
            {
                await tracker.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Metadados ilegíveis ou disco indisponível não podem derrubar o acompanhamento para sempre.
            }
            Restarts++;
            using var pause = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = scheduler.DelayAsync(RestartDelay, pause.Token).AsTask();
            var woken = wake.WaitAsync(pause.Token);
            await Task.WhenAny(delay, woken).ConfigureAwait(false);
            await pause.CancelAsync().ConfigureAwait(false);
            // Os dois lados terminam cancelados; observa para não deixar exceção sem leitura.
            await Task.WhenAll(delay, woken).ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (run is not null)
        {
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            // Se o ciclo ainda estiver ativo (espera longa), o token e o semáforo continuam em uso: não são descartados.
            if (finished != run)
                return;
            try { await run.ConfigureAwait(false); }
            catch (Exception) { }
        }
        wake.Dispose();
        cancellation.Dispose();
    }
}
