using System.ComponentModel;
using Whisper.Recorder.Core.Recording;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>The Windows host supplies dialogs; no deletion happens until their explicit confirmation.</summary>
public sealed class RecordingManagementViewModel(RecordingManagement management) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public DeleteRecordingConfirmation? DeleteConfirmation { get; private set; }
    public bool DeleteAllConfirmationRequired { get; private set; }
    public ManagementOutcome? Outcome { get; private set; }
    public string Message { get; private set; } = "Gerencie as gravações guardadas no PC.";

    public void RequestDelete(Guid id)
    {
        DeleteAllConfirmationRequired = false;
        DeleteConfirmation = management.PrepareDelete(id);
        Outcome = ManagementOutcome.ConfirmationRequired;
        Message = DeleteConfirmation.Message;
        Notify();
    }

    public void RequestDeleteAll()
    {
        DeleteConfirmation = null;
        DeleteAllConfirmationRequired = true;
        Outcome = ManagementOutcome.ConfirmationRequired;
        Message = RecordingManagement.DeleteAllMessage;
        Notify();
    }

    public void CancelConfirmation()
    {
        DeleteConfirmation = null;
        DeleteAllConfirmationRequired = false;
        Outcome = null;
        Message = "Nenhum dado foi apagado.";
        Notify();
    }

    public async Task RenameAsync(Guid id, string? title, CancellationToken cancellationToken = default)
    {
        SetOutcome(await management.RenameAsync(id, title, cancellationToken), "Título salvo.");
    }

    public async Task ConfirmDeleteAsync(bool pendingTranscriptionConfirmed = false, CancellationToken cancellationToken = default)
    {
        if (DeleteConfirmation is not { } confirmation) return;
        var outcome = await management.DeleteAsync(confirmation.RecordingId, confirmed: true,
            pendingTranscriptionConfirmed: pendingTranscriptionConfirmed, cancellationToken: cancellationToken);
        if (outcome == ManagementOutcome.ConfirmationRequired)
        {
            RequestDelete(confirmation.RecordingId);
            return;
        }
        DeleteConfirmation = null;
        SetOutcome(outcome, "Gravação apagada do PC.");
    }

    public async Task ConfirmDeleteAllAsync(CancellationToken cancellationToken = default)
    {
        if (!DeleteAllConfirmationRequired) return;
        var outcome = await management.DeleteAllAsync(confirmed: true, cancellationToken);
        DeleteAllConfirmationRequired = false;
        SetOutcome(outcome, "Todos os dados do app foram apagados do PC.");
    }

    private void SetOutcome(ManagementOutcome outcome, string completedMessage)
    {
        Outcome = outcome;
        Message = outcome switch
        {
            ManagementOutcome.Completed => completedMessage,
            ManagementOutcome.RecordingInProgress => "Pare a gravação em andamento antes de gerenciar os dados.",
            ManagementOutcome.ConfirmationRequired => "Confirme antes de apagar os dados.",
            _ => "Não foi possível concluir a operação. Confira o armazenamento e o cofre de credenciais. Alguns dados podem permanecer no PC."
        };
        Notify();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
