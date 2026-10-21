using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whisper.Recorder.Core.Recording;

public sealed class RecordingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string RootDirectory { get; }

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
        File.Move(temporary, MetadataPath(metadata.Id), overwrite: true);
    }

    /// <summary>Call once on application startup, before creating a recording session.</summary>
    public IReadOnlyList<RecordingMetadata> RecoverInterrupted()
    {
        foreach (var metadata in List().Where(m => m.State == RecordingState.Recording))
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
        return List();
    }

    public IReadOnlyList<RecordingMetadata> List() => Directory.EnumerateDirectories(RootDirectory)
        .Select(d => Path.Combine(d, "metadata.json"))
        .Where(File.Exists)
        .Select(p => JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(p), JsonOptions)!)
        .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).ToArray();
}
