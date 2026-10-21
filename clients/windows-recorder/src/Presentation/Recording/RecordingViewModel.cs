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
        session.Changed += OnChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRecording => session.IsRecording;
    public bool IsPaused => session.IsPaused;
    public TimeSpan Duration => session.Current?.Duration ?? TimeSpan.Zero;
    public RecordingState? State => session.Current?.State;
    public IReadOnlyDictionary<AudioSourceKind, float> Levels => session.Levels;
    public IReadOnlyDictionary<AudioSourceKind, bool> Sources => session.SourceAvailability;
    public IReadOnlyList<RecordingWarning> Warnings => session.Current?.Warnings ?? [];
    public IReadOnlyList<RecordingMetadata> Recordings => repository.List();
    public string? Error { get; private set; }
    public string? ThirdPartyNotice => IsRecording
        ? "Informe os participantes sobre a gravação. Você é responsável por cumprir as regras aplicáveis."
        : null;

    public async Task StartAsync(IReadOnlyList<AudioDevice> devices, CancellationToken cancellationToken = default)
    {
        Error = null;
        try { await session.StartAsync(devices, cancellationToken); }
        catch (InvalidOperationException ex) { Error = ex.Message; }
        catch (UnauthorizedAccessException)
        {
            Error = "Acesso ao microfone negado. Libere o acesso nas configurações de privacidade do Windows ou escolha só o áudio do sistema.";
        }
        OnChanged();
    }

    public void Pause() => session.Pause();
    public void Resume() => session.Resume();
    public Task StopAsync(CancellationToken cancellationToken = default) => session.StopAsync(cancellationToken);

    private void OnChanged()
    {
        if (context is not null && SynchronizationContext.Current != context)
            context.Post(_ => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)), null);
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose() => session.Changed -= OnChanged;
}
