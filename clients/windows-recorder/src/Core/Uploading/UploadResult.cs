namespace Whisper.Recorder.Core.Uploading;

public enum UploadProblem
{
    None, ConfigurationRequired, ConfirmationRequired, AlreadyProcessing, RetryLater,
    KeyRejected, SizeLimit, UnsupportedFormat, DurationLimit, ChecksumMismatch,
    SizeMismatch, Rejected, NotReceived, Conflict, NotFound, Network, Storage, Cancelled, Service
}

/// <summary>Only local, non-secret facts belong in the user's confirmation.</summary>
public sealed record UploadConfirmation(Guid RecordingId, string Title, TimeSpan Duration, long SizeBytes, bool HasTranscription);
public sealed record UploadProgress(long SentBytes, long TotalBytes);

public sealed record UploadResult(UploadProblem Problem, DateTimeOffset? RetryAt = null)
{
    public bool Succeeded => Problem == UploadProblem.None;
    public bool RequiresConfiguration => Problem is UploadProblem.ConfigurationRequired or UploadProblem.KeyRejected;
    public string Message => Problem switch
    {
        UploadProblem.None => "Gravação enviada. A transcrição está em processamento.",
        UploadProblem.ConfigurationRequired => "Configure o endereço da API e a chave antes de enviar.",
        UploadProblem.ConfirmationRequired => "Confirme o título, a duração e o tamanho. Esta gravação pode já ter uma transcrição.",
        UploadProblem.AlreadyProcessing => "Esta gravação já está em processamento.",
        UploadProblem.RetryLater => "Limite de envios atingido. Aguarde o prazo indicado antes de tentar de novo.",
        UploadProblem.KeyRejected => "A chave não foi aceita. Atualize a chave nas configurações.",
        UploadProblem.SizeLimit => "O arquivo excede o limite de 5 GiB.",
        UploadProblem.UnsupportedFormat => "O formato do áudio não é suportado pelo serviço.",
        UploadProblem.DurationLimit => "A duração excede o limite de 2 horas.",
        UploadProblem.ChecksumMismatch => "O conteúdo recebido não confere. Tente enviar de novo.",
        UploadProblem.SizeMismatch => "O tamanho recebido não confere. Tente enviar de novo.",
        UploadProblem.Rejected => "O envio anterior foi recusado. Tente enviar de novo.",
        UploadProblem.NotReceived => "O serviço ainda não recebeu o arquivo. Tente enviar de novo.",
        UploadProblem.Conflict => "Os dados diferem do pedido anterior. Confira a gravação antes de tentar de novo.",
        UploadProblem.NotFound => "O envio não está disponível no serviço. Confira o acesso e tente de novo.",
        UploadProblem.Network => "Não foi possível concluir o envio. A gravação está no PC. Tente de novo.",
        UploadProblem.Storage => "Não foi possível acessar os arquivos locais. Confira o armazenamento e tente de novo.",
        UploadProblem.Cancelled => "Envio interrompido. A gravação está no PC. Você pode tentar de novo.",
        _ => "O serviço não concluiu o envio. A gravação está no PC. Tente de novo."
    };
}
