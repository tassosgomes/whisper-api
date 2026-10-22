using System.ComponentModel;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;

namespace Whisper.Recorder.Presentation.Recording;

public sealed class RecordingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecordingSession session;
    private readonly RecordingRepository repository;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    public RecordingViewModel(RecordingSession session, RecordingRepository repository)
    {
        this.session = session;
        this.repository = repository;
        snapshot = session.Snapshot;
        session.Changed += OnChanged;
    }

    // Lido uma vez por atualização: estado, pausa, duração, níveis e avisos vêm do mesmo instante.
    private volatile SessionSnapshot snapshot;
    private volatile bool disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRecording => snapshot.IsRecording;
    public bool IsPaused => snapshot.IsPaused;
    public TimeSpan Duration => snapshot.Current?.Duration ?? TimeSpan.Zero;
    public RecordingState? State => snapshot.Current?.State;
    public Guid? CurrentId => snapshot.Current?.Id;
    public IReadOnlyDictionary<AudioSourceKind, float> Levels => snapshot.Levels;
    public IReadOnlyDictionary<AudioSourceKind, bool> Sources => snapshot.Availability;
    public IReadOnlyList<RecordingWarning> Warnings => snapshot.Current?.Warnings ?? [];
    public IReadOnlyList<RecordingMetadata> Recordings => repository.List();
    public string? Error { get; private set; }
    public string? ThirdPartyNotice => IsRecording
        ? "Informe os participantes sobre a gravação. Você é responsável por cumprir as regras aplicáveis."
        : null;

    public async Task StartAsync(IReadOnlyList<AudioDevice> devices, CancellationToken cancellationToken = default)
    {
        Error = null;
        try { await session.StartAsync(devices, cancellationToken); }
        catch (InvalidOperationException)
        {
            // Texto fixo: a mensagem da exceção pode vir da captura e conter dados do dispositivo.
            Error = devices.Count == 0
                ? "Escolha ao menos uma fonte de áudio para gravar."
                : "Não foi possível iniciar a gravação. Confira as fontes de áudio e tente de novo.";
        }
        catch (UnauthorizedAccessException)
        {
            Error = "Acesso ao microfone negado. Libere o acesso nas configurações de privacidade do Windows ou escolha só o áudio do sistema.";
        }
        Refresh();
    }

    public void Pause()
    {
        session.Pause();
        Refresh();
    }

    public void Resume()
    {
        session.Resume();
        Refresh();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try { await session.StopAsync(cancellationToken); }
        finally { Refresh(); }
    }

    // Ação do usuário concluída (thread da interface): relê o instantâneo na hora, sem esperar a coalescência.
    private void Refresh()
    {
        snapshot = session.Snapshot;
        OnChanged();
    }

    /// <summary>Intervalo mínimo entre atualizações da interface vindas da captura (cerca de 12 Hz).</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(80);

    private int scheduled;
    private long lastRaised;

    // A captura avisa a cada bloco e a cada nível (centenas de vezes por segundo). De outra thread, no máximo
    // uma atualização fica pendente e ela sempre lê o estado mais recente; na thread da interface (ou sem
    // contexto, nos testes) o aviso é imediato.
    private void OnChanged()
    {
        if (context is null || SynchronizationContext.Current == context)
        {
            Raise();
            return;
        }
        if (Interlocked.Exchange(ref scheduled, 1) == 1)
            return;
        _ = PostLaterAsync();
    }

    private async Task PostLaterAsync()
    {
        try
        {
            var wait = UpdateInterval - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastRaised));
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait).ConfigureAwait(false);
            context!.Post(_ =>
            {
                Volatile.Write(ref scheduled, 0);
                if (!disposed)
                    Raise();
            }, null);
        }
        catch
        {
            // Contexto encerrado: sem isto a coalescência ficaria presa e nenhuma atualização mais sairia.
            Volatile.Write(ref scheduled, 0);
        }
    }

    private void Raise()
    {
        snapshot = session.Snapshot;
        Interlocked.Exchange(ref lastRaised, Environment.TickCount64);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        disposed = true;
        session.Changed -= OnChanged;
    }
}
