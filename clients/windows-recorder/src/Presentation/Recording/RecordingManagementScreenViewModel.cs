using System.ComponentModel;
using System.Text.Json;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Uploading;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>Diálogos do Windows da gestão de dados. O host implementa; os testes usam dublês. Nada é apagado sem o "sim" daqui.</summary>
public interface IManagementDialogs
{
    /// <summary>Novo título, vazio para voltar ao padrão (data e hora), ou nulo se o usuário cancelou.</summary>
    Task<string?> PromptTitleAsync(string currentTitle);

    Task<bool> ConfirmDeleteAsync(DeleteRecordingConfirmation confirmation);

    /// <summary>Aviso específico: há envio ou transcrição em andamento e o job no serviço não é cancelado.</summary>
    Task<bool> ConfirmPendingDeleteAsync(DeleteRecordingConfirmation confirmation);

    Task<bool> ConfirmDeleteAllAsync(string message);
}

/// <summary>O que o host precisa liberar antes de o núcleo apagar arquivos: janelas de transcrição e player e o diário aberto.</summary>
public interface IManagementHost
{
    /// <summary>Fecha e libera o que usa os arquivos desta gravação (transcrição, player). Termina só com o arquivo liberado.</summary>
    Task ReleaseRecordingAsync(Guid id);

    /// <summary>Fecha tudo que usa arquivos do app, inclusive o diário. Chamado só depois da confirmação de apagar tudo.</summary>
    Task ReleaseAllAsync();

    /// <summary>Fim de "apagar tudo", com sucesso ou não. Se falhou, o diário volta a ser usado.</summary>
    void AfterDeleteAll(bool completed);
}

/// <summary>
/// Gestão na tela (RF-04, RF-08): reproduzir, renomear, apagar e apagar tudo. A regra e os avisos são do núcleo
/// (<see cref="RecordingManagementViewModel"/>); aqui ficam a ordem dos diálogos, a trava compartilhada
/// (<see cref="DataOperationGate"/>), a liberação de arquivos abertos e a atualização da lista e do estado de configuração.
/// Sem tipos WPF.
/// </summary>
public sealed class RecordingManagementScreenViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecordingManagementViewModel management;
    private readonly RecordingScreenViewModel screen;
    private readonly UploadScreenViewModel upload;
    private readonly ApiAccessViewModel access;
    private readonly IManagementDialogs dialogs;
    private readonly IManagementHost host;
    private readonly DataOperationGate gate;
    private string message = "";

    public RecordingManagementScreenViewModel(RecordingManagementViewModel management, RecordingScreenViewModel screen,
        UploadScreenViewModel upload, ApiAccessViewModel access, IManagementDialogs dialogs, IManagementHost host,
        DataOperationGate gate, string dataDirectory)
    {
        this.management = management;
        this.screen = screen;
        this.upload = upload;
        this.access = access;
        this.dialogs = dialogs;
        this.host = host;
        this.gate = gate;
        DataLocationText = "Os dados do app ficam neste PC, na pasta " + dataDirectory + ": gravações (áudio e transcrições), "
            + "configurações e o diário de diagnóstico. A chave de acesso fica no Gerenciador de Credenciais do Windows.";
        Play = new RelayCommand(RequestPlay, () => CanPlay);
        Rename = new AsyncRelayCommand(RenameAsync, () => CanManageSelected);
        Delete = new AsyncRelayCommand(DeleteAsync, () => CanManageSelected);
        DeleteAll = new AsyncRelayCommand(DeleteAllAsync, () => CanDeleteAll);
        screen.PropertyChanged += OnOtherChanged;
        upload.PropertyChanged += OnOtherChanged;
        gate.Changed += OnGateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>O usuário pediu para ouvir a gravação selecionada; o host abre o player.</summary>
    public event Action<Guid>? PlayRequested;
    /// <summary>Todos os dados do app foram apagados; as telas de configuração limpam os campos.</summary>
    public event Action? DataDeleted;

    public RelayCommand Play { get; }
    public AsyncRelayCommand Rename { get; }
    public AsyncRelayCommand Delete { get; }
    public AsyncRelayCommand DeleteAll { get; }

    public string DataLocationText { get; }
    public bool IsBusy => gate.IsBusy;
    public string Message => message;

    public bool CanPlay => !gate.IsBusy && upload.SelectedRecording is { State: not RecordingState.Recording };
    public bool CanManageSelected => !gate.IsBusy && !screen.IsRecording && upload.SelectedRecording is not null;
    public bool CanDeleteAll => !gate.IsBusy && !screen.IsRecording;

    /// <summary>Por que um comando está desligado, em texto: o estado nunca depende só da cor.</summary>
    public string Hint => gate.IsBusy ? "Aguarde: a operação sobre os dados do app está em andamento."
        : screen.IsRecording ? "Pare a gravação para renomear, apagar ou apagar todos os dados."
        : upload.SelectedRecording is null ? "Selecione uma gravação na lista para reproduzir, renomear ou apagar."
        : "";

    private void RequestPlay()
    {
        if (CanPlay && upload.SelectedRecordingId is { } id)
            PlayRequested?.Invoke(id);
    }

    private async Task RenameAsync()
    {
        if (!CanManageSelected || upload.SelectedRecording is not { } item || !gate.TryEnter())
            return;
        try
        {
            var title = await dialogs.PromptTitleAsync(item.Title);
            if (title is null)
            {
                Say("Nenhuma alteração.");
                return;
            }
            await management.RenameAsync(item.Id, title);
            Say(management.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Say("Não foi possível salvar o título. Confira se a gravação ainda existe no PC.");
        }
        finally
        {
            gate.Exit();
            screen.RefreshRecordings();
        }
    }

    private async Task DeleteAsync()
    {
        if (!CanManageSelected || upload.SelectedRecordingId is not { } id || !gate.TryEnter())
            return;
        try
        {
            management.RequestDelete(id);
            var confirmation = management.DeleteConfirmation!;
            if (!await dialogs.ConfirmDeleteAsync(confirmation))
            {
                management.CancelConfirmation();
                Say(management.Message);
                return;
            }
            var pendingConfirmed = false;
            if (confirmation.HasPendingTranscription)
            {
                // Aviso à parte: o job no serviço continua, só a cópia local é removida.
                if (!await dialogs.ConfirmPendingDeleteAsync(confirmation))
                {
                    management.CancelConfirmation();
                    Say(management.Message);
                    return;
                }
                pendingConfirmed = true;
            }
            // Só depois do aceite: para o envio local desta gravação e libera o que mantém o WAV ou a transcrição abertos.
            upload.ForgetRecording(id);
            await host.ReleaseRecordingAsync(id);
            await management.ConfirmDeleteAsync(pendingConfirmed);
            // Um envio pode ter começado depois do primeiro diálogo: o núcleo recusa e o aviso específico é mostrado agora.
            if (management.Outcome == ManagementOutcome.ConfirmationRequired
                && management.DeleteConfirmation is { HasPendingTranscription: true } again)
            {
                if (await dialogs.ConfirmPendingDeleteAsync(again))
                    await management.ConfirmDeleteAsync(pendingTranscriptionConfirmed: true);
                else
                    management.CancelConfirmation();
            }
            Say(management.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Say("Não foi possível ler esta gravação no PC. Ela pode já ter sido apagada.");
            management.CancelConfirmation();
        }
        finally
        {
            gate.Exit();
            screen.RefreshRecordings();
        }
    }

    private async Task DeleteAllAsync()
    {
        if (!CanDeleteAll || !gate.TryEnter())
            return;
        var released = false;
        var completed = false;
        try
        {
            management.RequestDeleteAll();
            if (!await dialogs.ConfirmDeleteAllAsync(RecordingManagement.DeleteAllMessage))
            {
                management.CancelConfirmation();
                Say(management.Message);
                return;
            }
            upload.ForgetRecording();
            released = true;
            await host.ReleaseAllAsync();
            await management.ConfirmDeleteAllAsync();
            completed = management.Outcome == ManagementOutcome.Completed;
            Say(management.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say("Não foi possível concluir a operação. Alguns dados podem permanecer no PC.");
        }
        finally
        {
            // A trava sempre é liberada, mesmo se o host, a lista ou a configuração lançarem aqui.
            try
            {
                if (released)
                    host.AfterDeleteAll(completed);
                // A lista e o estado de configuração refletem o que sobrou, com sucesso ou não.
                screen.RefreshRecordings();
                try { await access.InitializeAsync(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
            }
            finally
            {
                gate.Exit();
            }
            if (completed)
                DataDeleted?.Invoke();
        }
    }

    private void Say(string text)
    {
        message = text;
        Notify();
    }

    private void OnOtherChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Cronômetro e níveis chegam em alta frequência e não mudam o que os comandos permitem.
        if (e.PropertyName is null or nameof(RecordingScreenViewModel.IsRecording) or nameof(RecordingScreenViewModel.Recordings))
            Notify();
    }

    private void OnGateChanged() => Notify();

    private void Notify()
    {
        Play.RaiseCanExecuteChanged();
        Rename.RaiseCanExecuteChanged();
        Delete.RaiseCanExecuteChanged();
        DeleteAll.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        screen.PropertyChanged -= OnOtherChanged;
        upload.PropertyChanged -= OnOtherChanged;
        gate.Changed -= OnGateChanged;
    }
}
