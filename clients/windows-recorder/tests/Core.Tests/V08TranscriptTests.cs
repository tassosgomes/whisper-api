using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Transcripts;
using Whisper.Recorder.Presentation.Transcripts;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V08TranscriptTests
{
    [Fact]
    public void OpensLocalFilesWithSegmentsInOrderAndRecordingRelativeTimes()
    {
        using var rig = new LocalRig();
        var notifications = 0;
        rig.ViewModel.PropertyChanged += (_, _) => notifications++;
        rig.Open();
        Assert.Equal(rig.Id, rig.ViewModel.Recording!.Id);
        Assert.Equal(new long[] { 1240, 3661007 }, rig.ViewModel.Segments.Select(segment => segment.StartMs));
        Assert.Equal(new[] { "[00:00:01.240]", "[01:01:01.007]" }, rig.ViewModel.Segments.Select(segment => segment.Timestamp));
        Assert.Equal(new[] { "Olá reunião", "Outro trecho" }, rig.ViewModel.Segments.Select(segment => segment.Text));
        Assert.True(rig.ViewModel.CanCopy);
        Assert.True(rig.ViewModel.CanExport);
        Assert.False(rig.ViewModel.IsEmpty);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void CopiesCompleteTextWithTimestampsExactlyAsSavedByTracker()
    {
        using var rig = new LocalRig();
        rig.Open();
        Assert.Equal(LocalRig.Timestamped, rig.ViewModel.CopyText(includeTimestamps: true));
        rig.AssertUnchanged();
    }

    [Fact]
    public void CopiesCompleteTextWithoutTimestamps()
    {
        using var rig = new LocalRig();
        rig.Open();
        Assert.Equal("Olá reunião" + Environment.NewLine + "Outro trecho", rig.ViewModel.CopyText(includeTimestamps: false));
        rig.AssertUnchanged();
    }

    [Fact]
    public void ExportsNewFileAsReadableUtf8WithTimes()
    {
        using var rig = new LocalRig();
        rig.Open();
        var destination = Path.Combine(rig.Root, "ata.txt");
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(destination));
        Assert.Equal(Encoding.UTF8.GetBytes(LocalRig.Timestamped), File.ReadAllBytes(destination));
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.ExportOutcome);
        Assert.Equal("Transcrição exportada.", rig.ViewModel.Message);
        rig.AssertUnchanged();
    }

    [Fact]
    public void ExistingFileIsPreservedUntilOverwriteIsConfirmed()
    {
        using var rig = new LocalRig();
        rig.Open();
        var destination = Path.Combine(rig.Root, "ata.txt");
        File.WriteAllText(destination, "anotações existentes");
        Assert.Equal(TranscriptExportOutcome.ConfirmationRequired, rig.ViewModel.Export(destination));
        Assert.Equal("anotações existentes", File.ReadAllText(destination));
        Assert.Contains("Confirme", rig.ViewModel.Message);
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(destination, overwriteConfirmed: true));
        Assert.Equal(LocalRig.Timestamped, File.ReadAllText(destination));
        rig.AssertUnchanged();
    }

    [Fact]
    public void FileCreatedAfterOpeningStillRequiresOverwriteConfirmation()
    {
        using var rig = new LocalRig();
        rig.Open();
        var destination = Path.Combine(rig.Root, "ata.txt");
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(destination));
        Assert.Equal(TranscriptExportOutcome.ConfirmationRequired, rig.ViewModel.Export(destination));
        Assert.Equal(LocalRig.Timestamped, File.ReadAllText(destination));
    }

    [Fact]
    public void EmptyTranscriptShowsNoSpeechAndCopiesAndExportsEmptyText()
    {
        using var rig = new LocalRig();
        rig.WriteResult("{\"schemaVersion\":1,\"segments\":[]}", "");
        rig.Open();
        Assert.True(rig.ViewModel.IsEmpty);
        Assert.Empty(rig.ViewModel.Segments);
        Assert.Equal("Nenhuma fala foi reconhecida.", rig.ViewModel.Message);
        Assert.Equal("", rig.ViewModel.CopyText(true));
        Assert.Equal("", rig.ViewModel.CopyText(false));
        var destination = Path.Combine(rig.Root, "vazia.txt");
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(destination));
        Assert.Empty(File.ReadAllBytes(destination));
    }

    [Fact]
    public void WhitespaceOnlySegmentsAlsoShowNoSpeech()
    {
        using var rig = new LocalRig();
        rig.WriteResult("""{"schemaVersion":1,"segments":[{"startMs":0,"text":"  \n  "}]}""", "[00:00:00.000] " + Environment.NewLine);
        rig.Open();
        Assert.True(rig.ViewModel.IsEmpty);
        Assert.Equal("Nenhuma fala foi reconhecida.", rig.ViewModel.Message);
        Assert.Equal("", rig.ViewModel.CopyText(true));
    }

    [Fact]
    public void LocalTranscriptOpensAfterServiceRetentionAndAcrossReopenWithoutApiConfiguration()
    {
        using var rig = new LocalRig();
        rig.WriteMetadata(RecordingState.ResultExpired, DateTimeOffset.UtcNow.AddDays(-3));
        var reopened = new TranscriptViewModel(new LocalTranscriptStore(new RecordingRepository(rig.Root)));
        reopened.Open(rig.Id);
        Assert.Equal(RecordingState.ResultExpired, reopened.Recording!.State);
        Assert.True(reopened.CanCopy);
        Assert.Equal(LocalRig.Timestamped, reopened.CopyText(true));
        Assert.Equal(2, reopened.Segments.Count);
        Assert.False(reopened.UpdateRequired);
    }

    [Fact]
    public void UnknownSchemaWithoutTextFileRequestsUpdateAndPreservesRecordingAndRawResult()
    {
        using var rig = new LocalRig();
        const string raw = "{\n  \"schemaVersion\":99,\"future\":{\"content\":\"preservar\"}\n}";
        rig.WriteResult(raw, "");
        File.Delete(rig.TextPath);
        rig.Open();
        Assert.True(rig.ViewModel.UpdateRequired);
        Assert.Equal(rig.Id, rig.ViewModel.Recording!.Id);
        Assert.Contains("Atualize o app", rig.ViewModel.Message);
        Assert.Empty(rig.ViewModel.Segments);
        Assert.False(rig.ViewModel.IsEmpty);
        Assert.False(rig.ViewModel.CanCopy);
        Assert.False(rig.ViewModel.CanExport);
        Assert.Equal("", rig.ViewModel.CopyText(true));
        var destination = Path.Combine(rig.Root, "futura.txt");
        Assert.Equal(TranscriptExportOutcome.Unavailable, rig.ViewModel.Export(destination, overwriteConfirmed: true));
        Assert.False(File.Exists(destination));
        Assert.Equal(raw, File.ReadAllText(rig.JsonPath));
        Assert.False(File.Exists(rig.TextPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(rig.Repository.AudioPath(rig.Id)));
        Assert.Single(rig.Repository.List());
    }

    [Fact]
    public void OpeningUnknownVersionClearsPreviousTranscriptAndExportState()
    {
        using var rig = new LocalRig();
        rig.Open();
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(Path.Combine(rig.Root, "ata.txt")));
        rig.WriteResult("{\"schemaVersion\":2}", "texto antigo");
        rig.Open();
        Assert.Null(rig.ViewModel.ExportOutcome);
        Assert.Empty(rig.ViewModel.Segments);
        Assert.Equal("", rig.ViewModel.CopyText(false));
        Assert.False(rig.ViewModel.CanExport);
    }

    [Fact]
    public void MissingLocalFilesClearPreviousContentAndDoNotFallBackToService()
    {
        using var rig = new LocalRig();
        rig.Open();
        File.Delete(rig.TextPath);
        rig.Open();
        Assert.False(rig.ViewModel.CanCopy);
        Assert.False(rig.ViewModel.CanExport);
        Assert.Empty(rig.ViewModel.Segments);
        Assert.Equal("", rig.ViewModel.CopyText(true));
        Assert.Contains("Não foi possível ler", rig.ViewModel.Message);
    }

    [Theory]
    [InlineData("""{"segments":[]}""")]
    [InlineData("""{"schemaVersion":"2"}""")]
    [InlineData("""{"schemaVersion":1,"segments":[{"startMs":-5,"text":"a"}]}""")]
    public void InvalidLocalResultReportsReadFailureAndClearsPreviousContent(string json)
    {
        using var rig = new LocalRig();
        rig.Open();
        Assert.Equal(TranscriptExportOutcome.Saved, rig.ViewModel.Export(Path.Combine(rig.Root, "ata.txt")));
        rig.WriteResult(json, LocalRig.Timestamped);
        rig.Open();
        Assert.Equal("Não foi possível ler a transcrição local. Confira os arquivos no PC.", rig.ViewModel.Message);
        Assert.Null(rig.ViewModel.Recording);
        Assert.Null(rig.ViewModel.ExportOutcome);
        Assert.Empty(rig.ViewModel.Segments);
        Assert.False(rig.ViewModel.CanCopy);
        Assert.False(rig.ViewModel.CanExport);
        Assert.False(rig.ViewModel.UpdateRequired);
        Assert.Equal("", rig.ViewModel.CopyText(true));
        Assert.Equal(json, File.ReadAllText(rig.JsonPath));
    }

    [Fact]
    public void ExportStorageFailureIsReportedWithoutContentOrPathInMessage()
    {
        using var rig = new LocalRig();
        rig.Open();
        Assert.Equal(TranscriptExportOutcome.StorageError,
            rig.ViewModel.Export(Path.Combine(rig.Root, "não-existe", "ata.txt")));
        Assert.Equal("Não foi possível exportar a transcrição. Confira o local escolhido.", rig.ViewModel.Message);
        Assert.Equal(LocalRig.Timestamped, rig.ViewModel.CopyText(true));
        rig.AssertUnchanged();
    }

    [Fact]
    public void ExportBeforeOpeningDoesNotCreateFile()
    {
        using var rig = new LocalRig();
        var destination = Path.Combine(rig.Root, "ata.txt");
        Assert.Equal(TranscriptExportOutcome.Unavailable, rig.ViewModel.Export(destination));
        Assert.False(File.Exists(destination));
    }

    // Only local, disposable files, using the exact V-07 filenames and serialization conventions.
    // There are no network ports, credentials, API fixtures or background trackers in this suite.
    private sealed class LocalRig : IDisposable
    {
        public static readonly string Timestamped = "[00:00:01.240] Olá reunião" + Environment.NewLine
            + "[01:01:01.007] Outro trecho" + Environment.NewLine;
        private static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "whisper-v08-" + Guid.NewGuid().ToString("N"));
        public Guid Id { get; } = Guid.NewGuid();
        public RecordingRepository Repository { get; }
        public TranscriptViewModel ViewModel { get; }
        public string JsonPath => Path.Combine(Repository.DirectoryFor(Id), "result-v1.json");
        public string TextPath => Path.Combine(Repository.DirectoryFor(Id), "transcription.txt");
        private readonly Dictionary<string, byte[]> originals;

        public LocalRig()
        {
            Repository = new(Root);
            Directory.CreateDirectory(Repository.DirectoryFor(Id));
            WriteMetadata(RecordingState.Transcribed, DateTimeOffset.UtcNow);
            File.WriteAllBytes(Repository.AudioPath(Id), [1, 2, 3]);
            WriteResult(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                segments = new[] { new { startMs = 1240, text = "Olá reunião" }, new { startMs = 3661007, text = "Outro trecho" } }
            }), Timestamped);
            ViewModel = new(new LocalTranscriptStore(Repository));
            originals = Directory.EnumerateFiles(Repository.DirectoryFor(Id)).ToDictionary(path => path, File.ReadAllBytes);
        }

        public void WriteMetadata(RecordingState state, DateTimeOffset createdAt) => File.WriteAllText(
            Path.Combine(Repository.DirectoryFor(Id), "metadata.json"), JsonSerializer.Serialize(
                new RecordingMetadata(Id, "Reunião", createdAt, state, TimeSpan.FromMinutes(62), [], [],
                    JobId: Guid.NewGuid().ToString(), TrackingProblem: TrackingProblem.None), Options));

        public void WriteResult(string json, string text)
        {
            File.WriteAllText(JsonPath, json);
            File.WriteAllText(TextPath, text);
        }

        public void Open() => ViewModel.Open(Id);
        public void AssertUnchanged()
        {
            foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
