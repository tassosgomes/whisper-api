namespace Whisper.Recorder.Core.Access;

public enum ApiAccessStatus { Absent, Confirmed, Rejected, Unverifiable }
public enum ApiAccessProblem { None, InvalidAddress, StorageUnavailable }

/// <summary>Safe to bind or display: never contains the API key or a server error body.</summary>
public sealed record ApiAccessState(
    ApiAccessStatus Status,
    Uri? BaseAddress = null,
    string? MaskedKey = null,
    ApiAccessProblem Problem = ApiAccessProblem.None)
{
    public bool CanSend => Status == ApiAccessStatus.Confirmed;

    public string Message => Problem switch
    {
        ApiAccessProblem.InvalidAddress => "Informe um endereço HTTP ou HTTPS da API, sem usuário, senha, consulta ou fragmento.",
        ApiAccessProblem.StorageUnavailable => "Não foi possível acessar as configurações ou o cofre de credenciais. Tente novamente.",
        _ => Status switch
        {
            ApiAccessStatus.Confirmed => "Acesso à API confirmado.",
            ApiAccessStatus.Rejected => "A chave não foi aceita. Confira a chave ou informe uma nova nas configurações.",
            ApiAccessStatus.Unverifiable => "Não foi possível verificar o acesso à API. Confira a conexão e tente novamente.",
            _ => "Você pode gravar. Configure o acesso à API quando quiser enviar uma gravação."
        }
    };
}
