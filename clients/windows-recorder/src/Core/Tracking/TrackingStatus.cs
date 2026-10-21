using Whisper.Recorder.Core.Recording;

namespace Whisper.Recorder.Core.Tracking;

public enum TrackingProblem { None, Network, Service, ConfigurationRequired, Storage, SendRequired, UpdateRequired, EmptyTranscript }

public sealed record TrackingStatus(RecordingMetadata Recording, bool IsOffline)
{
    public bool CanResend => Recording.State is RecordingState.Failed or RecordingState.ResultExpired
        || Recording.TrackingProblem == TrackingProblem.SendRequired;
    public string Message => IsOffline ? "Sem conexão. O acompanhamento continua quando a conexão voltar." : Recording.State switch
    {
        RecordingState.Failed => FailureMessage + " A gravação está no PC. Você pode enviar de novo.",
        RecordingState.ResultExpired => "O resultado expirou no serviço. A gravação está no PC. Envie de novo para transcrever.",
        _ => Recording.TrackingProblem switch
        {
            TrackingProblem.ConfigurationRequired => "Confira o endereço e a chave nas configurações para continuar o acompanhamento.",
            TrackingProblem.UpdateRequired => "O resultado foi guardado no PC. Atualize o app para ler esta versão da transcrição.",
            TrackingProblem.EmptyTranscript => "Nenhuma fala reconhecida. O resultado foi guardado no PC.",
            TrackingProblem.SendRequired => "O envio foi interrompido. A gravação está no PC. Você pode enviar de novo.",
            TrackingProblem.Network => "Sem conexão. O acompanhamento será retomado automaticamente.",
            TrackingProblem.Service => "O serviço está indisponível. O acompanhamento será retomado automaticamente.",
            TrackingProblem.Storage => "Não foi possível guardar o resultado. Confira o armazenamento local; o app tentará de novo.",
            _ => Recording.State == RecordingState.Transcribed ? "Transcrição guardada no PC." : Recording.PublicJobStatus switch
            {
                "downloading" => "O serviço está obtendo o áudio.",
                "queued" => "Transcrição na fila.",
                _ => "Transcrição em processamento."
            }
        }
    };

    private string FailureMessage => Recording.FailureCode switch
    {
        "SOURCE_UNAVAILABLE" => "O serviço não conseguiu obter o áudio.",
        "MEDIA_SIZE_LIMIT_EXCEEDED" => "O áudio excede o limite de tamanho do serviço.",
        "MEDIA_DURATION_LIMIT_EXCEEDED" => "O áudio excede o limite de 2 horas do serviço.",
        "UNSUPPORTED_MEDIA_FORMAT" => "O serviço não reconheceu o formato do áudio.",
        _ => "O serviço não conseguiu transcrever o áudio."
    };
}
