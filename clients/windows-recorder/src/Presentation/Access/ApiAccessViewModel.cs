using System.ComponentModel;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Presentation.Access;

/// <summary>The host supplies the input secret to SaveAsync and clears its password control.</summary>
public sealed class ApiAccessViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ApiAccessService access;
    private readonly DataOperationGate? gate;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    public ApiAccessViewModel(ApiAccessService access, DataOperationGate? gate = null)
    {
        this.access = access;
        this.gate = gate;
        access.Changed += OnChanged;
        if (gate is not null)
            gate.Changed += OnChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ConfigurationRequested;
    public ApiAccessStatus Status => access.State.Status;
    public Uri? BaseAddress => access.State.BaseAddress;
    public string? MaskedKey => access.State.MaskedKey;
    public string Message => access.State.Message;
    public bool CanRecord => true;
    public bool CanSend => access.State.CanSend;
    /// <summary>Apagar dados do app em curso: salvar e remover a chave ficam desligados até terminar.</summary>
    public bool IsLocked => gate?.IsBusy == true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => access.CheckSavedAsync(cancellationToken);
    public Task SaveAsync(string? address, string apiKey, CancellationToken cancellationToken = default) =>
        IsLocked ? Task.CompletedTask : access.SaveAsync(address, apiKey, cancellationToken);
    public Task RemoveAsync(CancellationToken cancellationToken = default) =>
        IsLocked ? Task.CompletedTask : access.RemoveAsync(cancellationToken);

    /// <summary>Checks the saved key before an upload. Missing or rejected access opens settings.</summary>
    public async Task<bool> RequestSendAsync(CancellationToken cancellationToken = default)
    {
        await access.CheckSavedAsync(cancellationToken).ConfigureAwait(false);
        if (Status is ApiAccessStatus.Absent or ApiAccessStatus.Rejected)
            Dispatch(() => ConfigurationRequested?.Invoke());
        return CanSend;
    }

    private void OnChanged() => Dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)));

    private void Dispatch(Action action)
    {
        if (context is not null && SynchronizationContext.Current != context) context.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        access.Changed -= OnChanged;
        if (gate is not null)
            gate.Changed -= OnChanged;
    }
}
