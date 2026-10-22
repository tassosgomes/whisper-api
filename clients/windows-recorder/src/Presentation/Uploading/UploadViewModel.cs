using System.ComponentModel;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Presentation.Uploading;

/// <summary>The Windows host displays Confirmation before invoking ConfirmAndSendAsync.</summary>
public sealed class UploadViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UploadCoordinator coordinator;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private long lastPercent = -1;

    public UploadViewModel(UploadCoordinator coordinator)
    {
        this.coordinator = coordinator;
        coordinator.Changed += OnRecordingChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ConfigurationRequested;
    /// <summary>Os metadados de alguma gravação mudaram (estado, tentativa, problema). Chega no contexto da interface.</summary>
    public event Action? RecordingChanged;
    public UploadConfirmation? Confirmation { get; private set; }
    public UploadProgress? Progress { get; private set; }
    public UploadResult? Result { get; private set; }
    public bool IsSending { get; private set; }
    public string Message => Result?.Message ?? "Confirme os dados da gravação antes de enviar.";

    public void Prepare(Guid recordingId)
    {
        if (IsSending) return;
        Confirmation = coordinator.Prepare(recordingId);
        Progress = null;
        Result = null;
        Notify();
    }

    public async Task ConfirmAndSendAsync(bool existingTranscriptionConfirmed = false, CancellationToken cancellationToken = default)
    {
        if (IsSending || Confirmation is null) return;
        IsSending = true;
        Progress = null;
        Result = null;
        lastPercent = -1;
        Notify();
        try
        {
            var result = await coordinator.ConfirmAndSendAsync(Confirmation, existingTranscriptionConfirmed,
                new ProgressSink(Report), cancellationToken).ConfigureAwait(false);
            Dispatch(() =>
            {
                Result = result;
                if (result.RequiresConfiguration) ConfigurationRequested?.Invoke();
                Notify();
            });
        }
        finally { Dispatch(() => { IsSending = false; Notify(); }); }
    }

    // O PUT reporta a cada bloco (80 KiB): a tela só precisa ser avisada quando o percentual muda.
    private void Report(UploadProgress value)
    {
        var percent = value.TotalBytes <= 0 ? 100 : value.SentBytes * 100 / value.TotalBytes;
        if (percent == Interlocked.Exchange(ref lastPercent, percent)) return;
        Dispatch(() => { Progress = value; Notify(); });
    }

    private void OnRecordingChanged() => Dispatch(() => RecordingChanged?.Invoke());

    private void Dispatch(Action action)
    {
        if (context is not null && SynchronizationContext.Current != context) context.Post(_ => action(), null);
        else action();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() => coordinator.Changed -= OnRecordingChanged;

    private sealed class ProgressSink(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }
}
