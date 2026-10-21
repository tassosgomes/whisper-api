namespace Whisper.Recorder.Core.Ports;

/// <summary>Stores the single Whisper API key in the OS credential vault. Never log its value.</summary>
public interface ICredentialVault
{
    ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default);
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(CancellationToken cancellationToken = default);
}
