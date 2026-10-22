using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Presentation.Uploading;

/// <summary>Em que ponto está o envio da gravação escolhida; o painel só aparece fora de <see cref="Idle"/>.</summary>
public enum UploadPhase { Idle, Confirming, Sending, Accepted, Failed }

/// <summary>
/// Fluxo da tela de envio: Transcrever, confirmação, progresso, resultado e "tentar de novo". Nada sai sem
/// <see cref="ConfirmAsync"/> (ou <see cref="RetryAsync"/>, também um clique do usuário). Sem tipos WPF; o
/// texto fixo em português vem do núcleo (<see cref="UploadResult.Message"/>) e nunca contém a chave nem a URL de envio.
/// </summary>
public sealed class UploadScreenViewModel : INotifyPropertyChanged, IDisposable
{
    public const string ProcessingNotice =
        "O áudio desta gravação será enviado ao serviço de transcrição configurado e processado por ele. " +
        "Nada é enviado sem a sua confirmação.";

    public const string ExistingTranscriptionNotice =
        "Esta gravação já tem uma transcrição. Enviar de novo cria um novo processamento.";

    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    private readonly UploadViewModel upload;
    private readonly ApiAccessViewModel access;
    private readonly RecordingScreenViewModel screen;
    private CancellationTokenSource? cancellation;
    private Guid? selectedId;
    // A gravação do painel (confirmação, envio em curso e retry). A seleção da lista pode mudar sem redirecioná-los.
    private Guid? activeId;
    private UploadPhase phase;
    private string? hint;
    private string? notice;
    private bool existingConfirmed;

    public UploadScreenViewModel(UploadViewModel upload, ApiAccessViewModel access, RecordingScreenViewModel screen)
    {
        this.upload = upload;
        this.access = access;
        this.screen = screen;
        Transcribe = new AsyncRelayCommand(TranscribeAsync, () => CanTranscribe);
        Confirm = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        Retry = new AsyncRelayCommand(RetryAsync, () => CanRetry);
        Cancel = new RelayCommand(CancelOrClose, () => CanCancel);
        Dismiss = new RelayCommand(Close, () => CanDismiss);
        OpenSettings = new RelayCommand(() => ConfigurationRequested?.Invoke());
        upload.PropertyChanged += OnUploadChanged;
        upload.RecordingChanged += OnRecordingChanged;
        upload.ConfigurationRequested += OnConfigurationRequested;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Sem acesso configurado ou com a chave recusada: o host abre as configurações.</summary>
    public event Action? ConfigurationRequested;

    public AsyncRelayCommand Transcribe { get; }
    public AsyncRelayCommand Confirm { get; }
    public AsyncRelayCommand Retry { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Dismiss { get; }
    public RelayCommand OpenSettings { get; }

    public UploadPhase Phase => phase;
    public Guid? SelectedRecordingId => selectedId;
    public RecordingListItem? SelectedRecording => selectedId is { } id ? screen.Recordings.FirstOrDefault(r => r.Id == id) : null;
    public bool CanTranscribe => Phase == UploadPhase.Idle && !upload.IsSending && SelectedRecording is { CanTranscribe: true };
    public string TranscribeText => SelectedRecording?.State == Core.Recording.RecordingState.Transcribed
        ? "Enviar de novo para transcrição" : "Transcrever";
    /// <summary>Mensagem curta junto ao botão Transcrever (ex.: configurar o acesso antes do primeiro envio).</summary>
    public string? Hint => hint;

    public bool IsVisible => Phase != UploadPhase.Idle;
    public bool IsConfirming => Phase == UploadPhase.Confirming;
    public bool IsSending => Phase == UploadPhase.Sending;
    public bool IsAccepted => Phase == UploadPhase.Accepted;
    public bool IsFailed => Phase == UploadPhase.Failed;
    public bool CanConfirm => Phase == UploadPhase.Confirming && !upload.IsSending
        && (!HasTranscription || ExistingTranscriptionConfirmed);
    public bool CanRetry => Phase == UploadPhase.Failed && !upload.IsSending && upload.Confirmation is not null;
    public bool AsksExistingConfirmation => IsConfirming && HasTranscription;
    public bool CanCancel => Phase is UploadPhase.Confirming or UploadPhase.Sending;
    public bool CanDismiss => Phase is UploadPhase.Accepted or UploadPhase.Failed;
    public bool NeedsSettings => Phase == UploadPhase.Failed && upload.Result?.RequiresConfiguration == true;

    public string Heading => Phase switch
    {
        UploadPhase.Confirming => "Enviar para transcrição",
        UploadPhase.Sending => "Enviando",
        UploadPhase.Accepted => "Enviada: em processamento",
        UploadPhase.Failed => "Envio não concluído",
        _ => ""
    };

    public string? ConfirmationTitle => upload.Confirmation is { } c ? $"Título: {c.Title}" : null;
    public string? ConfirmationDuration => upload.Confirmation is { } c ? $"Duração: {DurationFormat.Format(c.Duration)}" : null;
    public string? ConfirmationSize => upload.Confirmation is { } c ? $"Tamanho aproximado: {SizeFormat.Format(c.SizeBytes)}" : null;
    public string Notice => ProcessingNotice;
    public bool HasTranscription => upload.Confirmation?.HasTranscription == true;
    public string ExistingNotice => ExistingTranscriptionNotice;

    /// <summary>O usuário reconhece que a gravação já tem transcrição e escolhe enviar de novo.</summary>
    public bool ExistingTranscriptionConfirmed
    {
        get => existingConfirmed;
        set
        {
            if (existingConfirmed == value) return;
            existingConfirmed = value;
            Notify();
        }
    }

    public bool IsIndeterminate => Phase == UploadPhase.Sending && upload.Progress is null;
    public bool ShowsProgress => Phase == UploadPhase.Sending;

    public double ProgressFraction => upload.Progress is { TotalBytes: > 0 } p
        ? Math.Clamp((double)p.SentBytes / p.TotalBytes, 0, 1) : 0;

    public string ProgressText => Phase switch
    {
        UploadPhase.Sending when upload.Progress is { } p =>
            $"Enviando… {(int)(ProgressFraction * 100)}% ({SizeFormat.Format(p.SentBytes)} de {SizeFormat.Format(p.TotalBytes)})",
        UploadPhase.Sending => "Preparando o envio…",
        _ => ""
    };

    /// <summary>Texto do resultado em linguagem simples. Em 429 acrescenta quando será possível tentar de novo.</summary>
    public string? ResultMessage
    {
        get
        {
            if (Phase is not (UploadPhase.Accepted or UploadPhase.Failed)) return notice;
            var message = upload.Result?.Message ?? new UploadResult(UploadProblem.Cancelled).Message;
            return upload.Result is { Problem: UploadProblem.RetryLater, RetryAt: { } at }
                ? $"{message} Disponível a partir das {at.ToLocalTime().ToString("HH:mm", Brazil)}."
                : message;
        }
    }

    /// <summary>Marca a gravação escolhida na lista; trocar de gravação fecha um painel que não está enviando.</summary>
    public void Select(RecordingListItem? item)
    {
        if (item is null) return;
        var changed = selectedId != item.Id;
        selectedId = item.Id;
        if (changed && Phase is not UploadPhase.Sending)
        {
            phase = UploadPhase.Idle;
            hint = null;
            notice = null;
        }
        Notify();
    }

    public async Task TranscribeAsync()
    {
        if (!CanTranscribe || selectedId is not { } id) return;
        hint = null;
        notice = null;
        try
        {
            // Sem acesso confirmado, RequestSendAsync pede as configurações (primeiro envio); nada é preparado.
            var ready = await access.RequestSendAsync();
            if (!ready && access.Status is ApiAccessStatus.Absent or ApiAccessStatus.Rejected)
            {
                hint = access.Status == ApiAccessStatus.Absent
                    ? "Configure o endereço da API e a chave para enviar. Depois toque em Transcrever de novo."
                    : "A chave não foi aceita. Atualize a chave nas configurações e toque em Transcrever de novo.";
                Notify();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            hint = "Não foi possível acessar as configurações. Tente novamente.";
            Notify();
            return;
        }
        // Acesso "não verificável" (sem rede agora) não impede a confirmação: o envio informa a falha.
        if (!Prepare(id)) return;
        activeId = id;
        existingConfirmed = false;
        phase = UploadPhase.Confirming;
        Notify();
    }

    public Task ConfirmAsync() => CanConfirm ? SendAsync() : Task.CompletedTask;

    public Task RetryAsync() => CanRetry ? SendAsync() : Task.CompletedTask;

    private bool Prepare(Guid id)
    {
        try
        {
            upload.Prepare(id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Texto fixo: a mensagem da exceção traz caminhos locais.
            phase = UploadPhase.Idle;
            hint = "Não foi possível ler esta gravação no PC. Confira se os arquivos ainda existem.";
            Notify();
            return false;
        }
    }

    private async Task SendAsync()
    {
        cancellation = new CancellationTokenSource();
        phase = UploadPhase.Sending;
        notice = null;
        Notify();
        try
        {
            await upload.ConfirmAndSendAsync(existingConfirmed, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelado antes de começar: sem resultado do núcleo, a tela mostra a mensagem de interrompido.
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
        }

        var result = upload.Result;
        if (result is { Problem: UploadProblem.ConfirmationRequired } && !upload.Confirmation!.HasTranscription
            && activeId is { } id && Prepare(id))
        {
            // A gravação mudou desde que o usuário confirmou: mostra os dados atuais e pede nova confirmação.
            notice = "Os dados da gravação mudaram. Confira de novo e confirme o envio.";
            phase = UploadPhase.Confirming;
        }
        else
            phase = result?.Succeeded == true ? UploadPhase.Accepted : UploadPhase.Failed;
        Notify();
    }

    private void CancelOrClose()
    {
        if (Phase == UploadPhase.Sending)
        {
            // O núcleo mantém a gravação e o ponto do envio; o resultado vira "interrompido" e há "tentar de novo".
            cancellation?.Cancel();
            return;
        }
        Close();
    }

    private void Close()
    {
        phase = UploadPhase.Idle;
        notice = null;
        Notify();
    }

    private void OnConfigurationRequested() => ConfigurationRequested?.Invoke();

    private void OnRecordingChanged()
    {
        screen.RefreshRecordings();
        Notify();
    }

    private void OnUploadChanged(object? sender, PropertyChangedEventArgs e) => Notify();

    private void Notify()
    {
        foreach (var command in new[] { Transcribe, Confirm, Retry })
            command.RaiseCanExecuteChanged();
        Cancel.RaiseCanExecuteChanged();
        Dismiss.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        upload.PropertyChanged -= OnUploadChanged;
        upload.RecordingChanged -= OnRecordingChanged;
        upload.ConfigurationRequested -= OnConfigurationRequested;
        cancellation?.Cancel();
    }
}

/// <summary>Tamanho legível em português (base 1024), usado na confirmação e no progresso.</summary>
public static class SizeFormat
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.0", Brazil)} {units[unit]}";
    }
}
