using System.ComponentModel;
using System.Text.Json;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Transcripts;

namespace Whisper.Recorder.Presentation.Transcripts;

/// <summary>The host supplies clipboard and file dialogs. All content comes from the local store.</summary>
public sealed class TranscriptViewModel(LocalTranscriptStore store) : INotifyPropertyChanged
{
    private LocalTranscript? transcript;
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
        transcript = null;
        Recording = null;
        ExportOutcome = null;
        try
        {
            transcript = store.Read(recordingId);
            Recording = transcript.Recording;
            Message = UpdateRequired ? "Atualize o app para ler esta versão da transcrição. A gravação está no PC."
                : IsEmpty ? "Nenhuma fala foi reconhecida." : "Transcrição guardada no PC.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Message = "Não foi possível ler a transcrição local. Confira os arquivos no PC.";
        }
        Notify();
    }

    public string CopyText(bool includeTimestamps) => CanCopy ? transcript!.CopyText(includeTimestamps) : "";

    public TranscriptExportOutcome Export(string path, bool overwriteConfirmed = false)
    {
        var result = transcript is null ? TranscriptExportOutcome.Unavailable : store.Export(transcript, path, overwriteConfirmed);
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

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
