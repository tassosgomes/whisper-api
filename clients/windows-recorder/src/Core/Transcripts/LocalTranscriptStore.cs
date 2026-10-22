using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Storage;

namespace Whisper.Recorder.Core.Transcripts;

public sealed record TranscriptSegment(long StartMs, string Text)
{
    public string Timestamp
    {
        get
        {
            var time = TimeSpan.FromTicks(StartMs * TimeSpan.TicksPerMillisecond);
            return string.Create(CultureInfo.InvariantCulture,
                $"[{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}]");
        }
    }
}

public sealed record LocalTranscript(RecordingMetadata Recording, bool UpdateRequired,
    IReadOnlyList<TranscriptSegment> Segments, string TimestampedText)
{
    public bool IsEmpty => !Segments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text));
    public string CopyText(bool includeTimestamps) => IsEmpty ? "" : includeTimestamps
        ? TimestampedText : string.Join(Environment.NewLine, Segments.Select(segment => segment.Text));
}

public enum TranscriptExportOutcome { Saved, ConfirmationRequired, Unavailable, StorageError }

/// <summary>Reads only the files saved by JobTracker; service retention and credentials play no part.</summary>
public sealed class LocalTranscriptStore(RecordingRepository recordings)
{
    public LocalTranscript Read(Guid recordingId)
    {
        var recording = recordings.Read(recordingId);
        var directory = recordings.DirectoryFor(recordingId);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "result-v1.json")));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new InvalidDataException();
        // A future result may not have segments or a .txt; keep the recording visible without interpreting it.
        if (number != 1) return new(recording, true, Array.Empty<TranscriptSegment>(), "");
        var segments = root.GetProperty("segments").EnumerateArray().Select(segment =>
        {
            var start = segment.GetProperty("startMs").GetInt64();
            if (start < 0 || start > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
                throw new InvalidDataException();
            var text = segment.GetProperty("text").GetString() ?? "";
            return new TranscriptSegment(start, text.Replace('\r', ' ').Replace('\n', ' ').Trim());
        }).ToArray();
        return new(recording, false, Array.AsReadOnly(segments),
            File.ReadAllText(Path.Combine(directory, "transcription.txt")));
    }

    /// <summary>
    /// The host must confirm replacement of the selected path before passing overwriteConfirmed. The text is fully written to a
    /// temporary file beside the destination first, so a failure never truncates an existing file.
    /// </summary>
    public TranscriptExportOutcome Export(LocalTranscript transcript, string path, bool overwriteConfirmed = false)
    {
        if (transcript.UpdateRequired) return TranscriptExportOutcome.Unavailable;
        string? temporary = null;
        try
        {
            var full = Path.GetFullPath(path);
            temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(transcript.CopyText(includeTimestamps: true)));
                file.Flush(true);
            }
            // Without confirmation the move refuses an existing destination, also one that appeared after the path was selected.
            if (overwriteConfirmed) AtomicFile.Replace(temporary, full);
            else File.Move(temporary, full, overwrite: false);
            return TranscriptExportOutcome.Saved;
        }
        catch (IOException) when (!overwriteConfirmed && File.Exists(path))
        {
            return TranscriptExportOutcome.ConfirmationRequired;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return TranscriptExportOutcome.StorageError;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
