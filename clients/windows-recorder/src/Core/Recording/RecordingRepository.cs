using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.Recorder.Core.Storage;

namespace Whisper.Recorder.Core.Recording;

public sealed class RecordingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string RootDirectory { get; }
    // The host shares this repository between upload and tracking coordinators.
    internal SemaphoreSlim Operations { get; } = new(1, 1);

    public RecordingRepository(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Whisper", "Recorder", "recordings");
        Directory.CreateDirectory(RootDirectory);
    }

    public string DirectoryFor(Guid id) => Path.Combine(RootDirectory, id.ToString("D"));
    public string AudioPath(Guid id) => Path.Combine(DirectoryFor(id), "audio.wav");
    private string MetadataPath(Guid id) => Path.Combine(DirectoryFor(id), "metadata.json");

    public RecordingMetadata Read(Guid id) => JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(MetadataPath(id)), JsonOptions)!;

    internal void Create(RecordingMetadata metadata)
    {
        Directory.CreateDirectory(DirectoryFor(metadata.Id));
        // Released before finalization on ENOSPC, leaving room for header and metadata.
        using (var reserve = new FileStream(ReservePath(metadata.Id), FileMode.CreateNew))
        {
            reserve.Write(new byte[65536]);
            reserve.Flush(true);
        }
        Save(metadata);
    }

    private string ReservePath(Guid id) => Path.Combine(DirectoryFor(id), ".reserve");
    internal void ReleaseReserve(Guid id) => File.Delete(ReservePath(id));

    internal void Save(RecordingMetadata metadata)
    {
        var temporary = MetadataPath(metadata.Id) + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, metadata, JsonOptions);
            output.Flush(true);
        }
        AtomicFile.Replace(temporary, MetadataPath(metadata.Id));
    }

    /// <summary>Call once on application startup, before creating a recording session. A corrupt metadata.json is skipped: it
    /// must not keep the other recordings stuck in "recording" (nor block "delete all" from the uninstaller).</summary>
    public IReadOnlyList<RecordingMetadata> RecoverInterrupted()
    {
        foreach (var metadata in ListReadable().Where(m => m.State == RecordingState.Recording))
        {
            ReleaseReserve(metadata.Id);
            if (File.Exists(AudioPath(metadata.Id)))
            {
                using var wav = new WavFile(AudioPath(metadata.Id), recover: true);
                Save(metadata with { State = RecordingState.Interrupted, Duration = TimeSpan.FromSeconds(wav.Frames / 16000d) });
            }
            else
                Save(metadata with { State = RecordingState.Interrupted, Duration = TimeSpan.Zero });
        }
        return ListReadable();
    }

    public IReadOnlyList<RecordingMetadata> List() => !Directory.Exists(RootDirectory) ? [] : Directory.EnumerateDirectories(RootDirectory)
        .Select(d => Path.Combine(d, "metadata.json"))
        .Where(File.Exists)
        .Select(p => JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(p), JsonOptions)!)
        .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).ToArray();

    /// <summary>
    /// Like <see cref="List"/>, but a recording whose metadata is corrupt, missing or locked is skipped instead of failing the
    /// whole listing. Used by background work (tracking) that must not stop because of one bad recording.
    /// </summary>
    internal IReadOnlyList<RecordingMetadata> ListReadable()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var result = new List<RecordingMetadata>();
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(RootDirectory).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        foreach (var directory in directories)
        {
            try
            {
                var path = Path.Combine(directory, "metadata.json");
                if (File.Exists(path) && JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path), JsonOptions) is { } metadata)
                    result.Add(metadata);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).ToArray();
    }
}
