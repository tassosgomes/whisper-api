using System.IO;
using System.Text.Json;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Access;

namespace Whisper.Recorder.Host.Setup;

/// <summary>
/// Modo sem interface usado pelo desinstalador (`WhisperRecorder.exe --purge-data`) quando o usuário escolhe
/// não manter as gravações. Reaproveita a remoção da 16.0 (chave primeiro, junções sem seguir o destino).
/// Nunca roda com o app aberto: a instância única também é a trava dos dados.
/// </summary>
public sealed class UninstallDataPurge(string dataDirectory, ICredentialVault vault, string mutexName = UninstallDataPurge.AppMutexName)
{
    /// <summary>Argumento de linha de comando que liga o modo. O script do instalador usa o mesmo texto.</summary>
    public const string Argument = "--purge-data";

    /// <summary>Mutex da instância única do app; o `AppMutex` do instalador usa o mesmo nome.</summary>
    public const string AppMutexName = "Whisper.Recorder.Host";

    public const int Success = 0;
    public const int AppRunning = 2;
    public const int StorageFailure = 3;

    /// <summary>Pasta de dados do app em produção: `%LOCALAPPDATA%\Whisper\Recorder`.</summary>
    public static string ProductionDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Whisper", "Recorder");

    public static UninstallDataPurge ForProduction() =>
        new(ProductionDataDirectory, new WindowsCredentialVault(WindowsCredentialVault.ProductionTarget));

    /// <summary>Devolve o código de saída do processo: 0 apagou (ou nada havia), 2 app aberto, 3 falha ao apagar.</summary>
    public int Run()
    {
        using var mutex = new Mutex(true, mutexName, out var created);
        if (!created)
            return AppRunning; // Com o app aberto nada é apagado.

        try
        {
            return PurgeAsync().GetAwaiter().GetResult();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private async Task<int> PurgeAsync()
    {
        try
        {
            if (!Directory.Exists(dataDirectory))
            {
                // Pasta ausente não é criada; a chave, que vive fora dela, ainda precisa sair do cofre.
                await vault.RemoveAsync().ConfigureAwait(false);
                return Success;
            }

            var repository = new RecordingRepository(Path.Combine(dataDirectory, "recordings"));
            try
            {
                // Uma gravação que ficou em "recording" (queda) não pode bloquear o "apagar tudo".
                repository.RecoverInterrupted();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Metadados ilegíveis não impedem a remoção: tudo sob a pasta é apagado de qualquer forma.
            }

            var outcome = await new RecordingManagement(repository, vault, dataDirectory)
                .DeleteAllAsync(confirmed: true).ConfigureAwait(false);
            if (outcome != ManagementOutcome.Completed)
                return StorageFailure;

            RemoveIfEmpty(dataDirectory);
            var parent = Path.GetDirectoryName(Path.GetFullPath(dataDirectory));
            if (parent is not null && Path.GetFileName(parent) == "Whisper")
                RemoveIfEmpty(parent);
            return Success;
        }
        catch (Exception)
        {
            // Sem detalhes: mensagens de erro podem conter caminhos locais.
            return StorageFailure;
        }
    }

    /// <summary>Remove a pasta só se estiver vazia; sobra silenciosa não é falha.</summary>
    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
