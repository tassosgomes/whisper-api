namespace Whisper.Recorder.Presentation.Recording;

/// <summary>
/// Trava compartilhada das operações sobre os dados locais (apagar uma gravação, apagar tudo). Enquanto ocupada, a captura,
/// o envio e a configuração do acesso ficam desligados: a limpeza não pode competir com novos arquivos ou com a chave.
/// Entra e sai na thread da interface; <see cref="Changed"/> reavalia os comandos.
/// </summary>
public sealed class DataOperationGate
{
    private int busy;

    public bool IsBusy => Volatile.Read(ref busy) == 1;
    public event Action? Changed;

    /// <summary>Falso se outra operação já está em curso.</summary>
    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return false;
        Changed?.Invoke();
        return true;
    }

    public void Exit()
    {
        Volatile.Write(ref busy, 0);
        Changed?.Invoke();
    }
}
