using System.ComponentModel;
using System.Text.Json;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Transcripts;

namespace Whisper.Recorder.Presentation.Transcripts;

/// <summary>The host supplies clipboard and file dialogs. All content comes from the local store.</summary>
public sealed class TranscriptViewModel(LocalTranscriptStore store) : INotifyPropertyChanged
{
    private LocalTranscript? transcript;
    // Only the latest Open/OpenAsync may publish its result: a slow read must not replace a newer choice.
    private int generation;
    public event PropertyChangedEventHandler? PropertyChanged;
    public RecordingMetadata? Recording { get; private set; }
    public IReadOnlyList<TranscriptSegment> Segments => transcript?.Segments ?? Array.Empty<TranscriptSegment>();
    public bool UpdateRequired => transcript?.UpdateRequired == true;
    public bool IsEmpty => transcript is { UpdateRequired: false, IsEmpty: true };
    public bool CanCopy => transcript is { UpdateRequired: false };
    public bool CanExport => CanCopy;
    public TranscriptExportOutcome? ExportOutcome { get; private set; }
    public string Message { get; private set; } = "Abra uma transcrição guardada no PC.";

    public void Open(Guid recordingId)
    {
        generation++;
        Reset();
        try
        {
            Apply(store.Read(recordingId));
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            Fail();
        }
        Notify();
    }

    /// <summary>Reads the files outside the calling (UI) thread; the state is published on the caller's context.</summary>
    public async Task OpenAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        var current = ++generation;
        Reset();
        Notify();
        try
        {
            var read = await Task.Run(() => store.Read(recordingId), cancellationToken);
            if (current != generation) return;
            Apply(read);
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            if (current != generation) return;
            Fail();
        }
        Notify();
    }

    public string CopyText(bool includeTimestamps) => CanCopy ? transcript!.CopyText(includeTimestamps) : "";

    public TranscriptExportOutcome Export(string path, bool overwriteConfirmed = false)
    {
        var result = transcript is null ? TranscriptExportOutcome.Unavailable : store.Export(transcript, path, overwriteConfirmed);
        return Report(result);
    }

    /// <summary>Writes the file outside the calling (UI) thread: the destination can be a slow network folder.</summary>
    public async Task<TranscriptExportOutcome> ExportAsync(string path, bool overwriteConfirmed = false)
    {
        var current = transcript;
        var result = current is null
            ? TranscriptExportOutcome.Unavailable
            : await Task.Run(() => store.Export(current, path, overwriteConfirmed));
        return Report(result);
    }

    private TranscriptExportOutcome Report(TranscriptExportOutcome result)
    {
        ExportOutcome = result;
        Message = result switch
        {
            TranscriptExportOutcome.Saved => "Transcrição exportada.",
            TranscriptExportOutcome.ConfirmationRequired => "O arquivo já existe. Confirme antes de sobrescrever.",
            TranscriptExportOutcome.StorageError => "Não foi possível exportar a transcrição. Confira o local escolhido.",
            _ => UpdateRequired ? "Atualize o app para ler esta versão da transcrição. A gravação está no PC."
                : "Abra uma transcrição guardada no PC para exportar."
        };
        Notify();
        return result;
    }

    private void Reset()
    {
        transcript = null;
        Recording = null;
        ExportOutcome = null;
    }

    private void Apply(LocalTranscript read)
    {
        transcript = read;
        Recording = read.Recording;
        Message = UpdateRequired ? "Atualize o app para ler esta versão da transcrição. A gravação está no PC."
            : IsEmpty ? "Nenhuma fala foi reconhecida." : "Transcrição guardada no PC.";
    }

    private void Fail() => Message = "Não foi possível ler a transcrição local. Confira os arquivos no PC.";

    private static bool IsReadFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or InvalidOperationException or KeyNotFoundException or FormatException;

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
