using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Recording;

public enum ManagementOutcome { Completed, ConfirmationRequired, RecordingInProgress, StorageError }

public sealed record DeleteRecordingConfirmation(Guid RecordingId, string Title, bool HasPendingTranscription)
{
    public string Message => HasPendingTranscription
        ? "Há envio ou transcrição em andamento. Apagar remove apenas a cópia no PC e não cancela o job no serviço. Não é possível desfazer. Confirme especificamente para continuar."
        : "Apagar remove o áudio, a transcrição e os metadados do PC. Não é possível desfazer.";
}

/// <summary>Local management only. Share the repository with upload and tracking coordinators.</summary>
public sealed class RecordingManagement
{
    private readonly RecordingRepository recordings;
    private readonly ICredentialVault vault;
    private readonly string appDirectory;

    /// <param name="appDirectory">App-owned data directory containing recordings, settings and diagnostics.</param>
    public RecordingManagement(RecordingRepository recordings, ICredentialVault vault, string appDirectory)
    {
        this.recordings = recordings;
        this.vault = vault;
        this.appDirectory = Path.GetFullPath(appDirectory);
        var relative = Path.GetRelativePath(this.appDirectory, Path.GetFullPath(recordings.RootDirectory));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("As gravações devem estar dentro do diretório de dados do app.", nameof(appDirectory));
    }

    public const string DeleteAllMessage = "Apagar todos os dados remove as gravações, transcrições, configurações e a chave de acesso do PC. Não cancela jobs no serviço. Não é possível desfazer. Confirme para continuar.";

    public DeleteRecordingConfirmation PrepareDelete(Guid id)
    {
        var recording = recordings.Read(id);
        return new(id, recording.DisplayTitle, Pending(recording));
    }

    public async Task<ManagementOutcome> RenameAsync(Guid id, string? title, CancellationToken cancellationToken = default)
    {
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recording = recordings.Read(id);
            if (recording.State == RecordingState.Recording) return ManagementOutcome.RecordingInProgress;
            recordings.Save(recording with { Title = title?.Trim() ?? "" });
            return ManagementOutcome.Completed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ManagementOutcome.StorageError; }
        finally { recordings.Operations.Release(); }
    }

    public async Task<ManagementOutcome> DeleteAsync(Guid id, bool confirmed = false,
        bool pendingTranscriptionConfirmed = false, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return ManagementOutcome.ConfirmationRequired;
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recording = recordings.Read(id);
            if (recording.State == RecordingState.Recording) return ManagementOutcome.RecordingInProgress;
            // Recheck under the same lock as upload/tracking: an earlier dialog may be stale.
            if (Pending(recording) && !pendingTranscriptionConfirmed) return ManagementOutcome.ConfirmationRequired;
            DeleteTree(recordings.DirectoryFor(id));
            return ManagementOutcome.Completed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ManagementOutcome.StorageError; }
        finally { recordings.Operations.Release(); }
    }

    public async Task<ManagementOutcome> DeleteAllAsync(bool confirmed = false, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return ManagementOutcome.ConfirmationRequired;
        await recordings.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (recordings.List().Any(recording => recording.State == RecordingState.Recording))
                return ManagementOutcome.RecordingInProgress;
            cancellationToken.ThrowIfCancellationRequested();
            // Once deletion starts, finish it even if the caller closes the dialog.
            // Remove the credential first: vault failure must not look like a successful reset.
            await vault.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
            if (Directory.Exists(appDirectory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(appDirectory).ToArray()) DeleteEntry(entry);
            }
            return ManagementOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ManagementOutcome.StorageError; }
        finally { recordings.Operations.Release(); }
    }

    /// <summary>
    /// Remove um arquivo, link ou pasta sem seguir links. Junções e links simbólicos dentro dos dados do app são apagados
    /// como links: o destino, que pode estar fora da pasta do app, nunca é tocado (nem o `Directory.Delete` recursivo
    /// do .NET dá essa garantia para links aninhados: ele falha com acesso negado).
    /// </summary>
    private static void DeleteTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToArray())
            DeleteEntry(entry);
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Link: só ele é removido. Link para pasta se apaga como pasta vazia; link para arquivo, como arquivo.
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
            else File.Delete(path);
        }
        else if ((attributes & FileAttributes.Directory) != 0) DeleteTree(path);
        else File.Delete(path);
    }

    private static bool Pending(RecordingMetadata recording) => recording.State is RecordingState.Uploading or RecordingState.Processing;
}
