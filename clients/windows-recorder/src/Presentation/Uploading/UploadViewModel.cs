using System.ComponentModel;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Presentation.Uploading;

/// <summary>The Windows host displays Confirmation before invoking ConfirmAndSendAsync.</summary>
public sealed class UploadViewModel(UploadCoordinator coordinator) : INotifyPropertyChanged
{
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ConfigurationRequested;
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
        Notify();
        try
        {
            var result = await coordinator.ConfirmAndSendAsync(Confirmation, existingTranscriptionConfirmed,
                new ProgressSink(value => Dispatch(() => { Progress = value; Notify(); })), cancellationToken).ConfigureAwait(false);
            Dispatch(() =>
            {
                Result = result;
                if (result.RequiresConfiguration) ConfigurationRequested?.Invoke();
                Notify();
            });
        }
        finally { Dispatch(() => { IsSending = false; Notify(); }); }
    }

    private void Dispatch(Action action)
    {
        if (context is not null && SynchronizationContext.Current != context) context.Post(_ => action(), null);
        else action();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private sealed class ProgressSink(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }
}
