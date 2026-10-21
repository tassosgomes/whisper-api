using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tests.Doubles;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Presentation.Recording;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V09ManagementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RenamePersistsAfterReopeningAndPreservesAudioTextResultAndJob()
    {
        using var rig = new ManagementRig();
        var original = rig.Repository.Read(rig.Id);
        var contents = rig.Contents();
        await rig.ViewModel.RenameAsync(rig.Id, "  Planejamento  ", Token);
        Assert.Equal(ManagementOutcome.Completed, rig.ViewModel.Outcome);
        Assert.Equal(JsonSerializer.Serialize(original with { Title = "Planejamento" }),
            JsonSerializer.Serialize(new RecordingRepository(rig.Repository.RootDirectory).Read(rig.Id)));
        rig.AssertContents(contents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task OptionalTitleUsesCreationDateAndTime(string? title)
    {
        using var rig = new ManagementRig();
        Assert.Equal(ManagementOutcome.Completed, await rig.Management.RenameAsync(rig.Id, title, Token));
        var metadata = new RecordingRepository(rig.Repository.RootDirectory).Read(rig.Id);
        Assert.Equal(metadata.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), metadata.DisplayTitle);
        Assert.Equal(metadata.DisplayTitle, rig.Management.PrepareDelete(rig.Id).Title);
        Assert.Equal(metadata.DisplayTitle, rig.Upload.Prepare(rig.Id).Title);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndWarnsThatItCannotBeUndone()
    {
        using var rig = new ManagementRig();
        var contents = rig.Contents();
        rig.ViewModel.RequestDelete(rig.Id);
        Assert.Contains("Não é possível desfazer", rig.ViewModel.Message);
        Assert.Equal(ManagementOutcome.ConfirmationRequired, await rig.Management.DeleteAsync(rig.Id, cancellationToken: Token));
        Assert.True(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
        rig.AssertContents(contents);
        rig.ViewModel.CancelConfirmation();
        await rig.ViewModel.ConfirmDeleteAsync(cancellationToken: Token);
        Assert.True(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
    }

    [Fact]
    public async Task DeleteRemovesEntireRecordingAndPreservesOtherRecordingAndConfiguration()
    {
        using var rig = new ManagementRig();
        var otherId = rig.AddRecording(RecordingState.Saved);
        var other = File.ReadAllBytes(rig.Repository.AudioPath(otherId));
        rig.ViewModel.RequestDelete(rig.Id);
        await rig.ViewModel.ConfirmDeleteAsync(cancellationToken: Token);
        Assert.Equal(ManagementOutcome.Completed, rig.ViewModel.Outcome);
        Assert.False(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
        Assert.Equal(otherId, Assert.Single(rig.Repository.List()).Id);
        Assert.Equal(other, File.ReadAllBytes(rig.Repository.AudioPath(otherId)));
        Assert.NotNull(rig.Configuration.Read());
        Assert.Equal("test-key", await rig.Vault.ReadAsync(Token));
        Assert.Equal(0, rig.Http.Requests);
    }

    [Theory]
    [InlineData(RecordingState.Uploading)]
    [InlineData(RecordingState.Processing)]
    public async Task PendingTranscriptionNeedsSpecificConfirmationAndDeletionNeverCallsService(RecordingState state)
    {
        using var rig = new ManagementRig();
        rig.SetState(state);
        rig.ViewModel.RequestDelete(rig.Id);
        Assert.True(rig.ViewModel.DeleteConfirmation!.HasPendingTranscription);
        Assert.Contains("não cancela o job no serviço", rig.ViewModel.Message);
        await rig.ViewModel.ConfirmDeleteAsync(cancellationToken: Token);
        Assert.Equal(ManagementOutcome.ConfirmationRequired, rig.ViewModel.Outcome);
        Assert.True(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
        await rig.ViewModel.ConfirmDeleteAsync(pendingTranscriptionConfirmed: true, Token);
        Assert.Equal(ManagementOutcome.Completed, rig.ViewModel.Outcome);
        await rig.Tracker.PollOnceAsync(Token);
        Assert.Empty(rig.Repository.List());
        Assert.Equal(0, rig.Http.Requests);
    }

    [Fact]
    public async Task UploadStartedAfterDialogRequiresNewSpecificConfirmation()
    {
        using var rig = new ManagementRig();
        rig.ViewModel.RequestDelete(rig.Id);
        Assert.False(rig.ViewModel.DeleteConfirmation!.HasPendingTranscription);
        rig.SetState(RecordingState.Uploading);
        await rig.ViewModel.ConfirmDeleteAsync(cancellationToken: Token);
        Assert.Equal(ManagementOutcome.ConfirmationRequired, rig.ViewModel.Outcome);
        Assert.True(rig.ViewModel.DeleteConfirmation!.HasPendingTranscription);
        Assert.True(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
        Assert.Equal(0, rig.Http.Requests);
    }

    [Fact]
    public async Task DeleteAllWithoutConfirmationPreservesFilesConfigurationAndKey()
    {
        using var rig = new ManagementRig();
        Assert.Equal(ManagementOutcome.ConfirmationRequired, await rig.Management.DeleteAllAsync(cancellationToken: Token));
        await rig.ViewModel.ConfirmDeleteAllAsync(Token);
        rig.ViewModel.RequestDeleteAll();
        Assert.Contains("Não é possível desfazer", rig.ViewModel.Message);
        Assert.Contains("Não cancela jobs", rig.ViewModel.Message);
        rig.ViewModel.CancelConfirmation();
        await rig.ViewModel.ConfirmDeleteAllAsync(Token);
        Assert.Single(rig.Repository.List());
        Assert.NotNull(rig.Configuration.Read());
        Assert.Equal("test-key", await rig.Vault.ReadAsync(Token));
    }

    [Fact]
    public async Task DeleteAllEmptiesAppDirectoryRemovesVaultKeyAndAllowsFreshUse()
    {
        using var rig = new ManagementRig();
        rig.AddRecording(RecordingState.Processing);
        var diagnostics = Path.Combine(rig.Root, "diagnostics");
        Directory.CreateDirectory(diagnostics);
        File.WriteAllText(Path.Combine(diagnostics, "events.log"), "ApplicationStarted");
        rig.ViewModel.RequestDeleteAll();
        await rig.ViewModel.ConfirmDeleteAllAsync(Token);
        Assert.Equal(ManagementOutcome.Completed, rig.ViewModel.Outcome);
        Assert.True(Directory.Exists(rig.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(rig.Root));
        Assert.Empty(rig.Repository.List());
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.Null(rig.Configuration.Read());
        await rig.Tracker.PollOnceAsync(Token);
        Assert.Equal(0, rig.Http.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(rig.Root));
        rig.Configuration.Save(new Uri("https://new.example/v1"));
        var freshId = rig.AddRecording(RecordingState.Saved);
        Assert.Equal(ManagementOutcome.Completed, await rig.Management.RenameAsync(freshId, "Nova reunião", Token));
        Assert.Equal("Nova reunião", Assert.Single(rig.Repository.List()).DisplayTitle);
    }

    [Fact]
    public async Task ActiveRecordingMustBeStoppedBeforeRenameDeleteOrReset()
    {
        using var rig = new ManagementRig();
        rig.SetState(RecordingState.Recording);
        Assert.Equal(ManagementOutcome.RecordingInProgress, await rig.Management.RenameAsync(rig.Id, "Outro título", Token));
        Assert.Equal(ManagementOutcome.RecordingInProgress, await rig.Management.DeleteAsync(rig.Id, confirmed: true, cancellationToken: Token));
        Assert.Equal(ManagementOutcome.RecordingInProgress, await rig.Management.DeleteAllAsync(confirmed: true, Token));
        Assert.Equal("Reunião", rig.Repository.Read(rig.Id).Title);
        Assert.Equal("test-key", await rig.Vault.ReadAsync(Token));
        Assert.Equal(0, rig.Http.Requests);
    }

    [Fact]
    public async Task VaultFailureDoesNotReportSuccessOrDiscardLocalData()
    {
        using var rig = new ManagementRig();
        var management = new RecordingManagement(rig.Repository, new UnavailableVault(), rig.Root);
        var viewModel = new RecordingManagementViewModel(management);
        viewModel.RequestDeleteAll();
        await viewModel.ConfirmDeleteAllAsync(Token);
        Assert.Equal(ManagementOutcome.StorageError, viewModel.Outcome);
        Assert.DoesNotContain("private exception", viewModel.Message);
        Assert.Single(rig.Repository.List());
        Assert.NotNull(rig.Configuration.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionWaitsForInflightPollAndNoLateWriteRecreatesFiles(bool all)
    {
        using var rig = new ManagementRig();
        rig.SetState(RecordingState.Processing);
        rig.Http.Pause = true;
        var polling = rig.Tracker.PollOnceAsync(Token);
        await rig.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var deleting = all ? rig.Management.DeleteAllAsync(confirmed: true, Token)
            : rig.Management.DeleteAsync(rig.Id, confirmed: true, pendingTranscriptionConfirmed: true, Token);
        Assert.False(deleting.IsCompleted);
        rig.Http.Continue.TrySetResult();
        await polling;
        Assert.Equal(ManagementOutcome.Completed, await deleting);
        await rig.Tracker.PollOnceAsync(Token);
        Assert.Equal(1, rig.Http.Requests);
        Assert.False(Directory.Exists(rig.Repository.DirectoryFor(rig.Id)));
        Assert.Empty(rig.Repository.List());
        if (all) Assert.Empty(Directory.EnumerateFileSystemEntries(rig.Root));
    }

    [Fact]
    public void AppRootMustContainRecordingsBeforeItCanBeReset()
    {
        using var rig = new ManagementRig();
        Assert.Throws<ArgumentException>(() => new RecordingManagement(rig.Repository, rig.Vault, rig.Repository.RootDirectory));
        Assert.Throws<ArgumentException>(() => new RecordingManagement(rig.Repository, rig.Vault, rig.Repository.RootDirectory + "-other"));
    }

    private sealed class UnavailableVault : ICredentialVault
    {
        public ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default) => throw new IOException("private exception");
        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) => throw new IOException("private exception");
        public ValueTask RemoveAsync(CancellationToken cancellationToken = default) => throw new IOException("private exception");
    }

    private sealed class Connected : IConnectivity { public bool IsConnected => true; }

    private sealed class HttpTap : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Entered.TrySetResult();
            if (Pause) await Continue.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"jobId\":\"job-1\",\"status\":\"processing\"}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ManagementRig : IDisposable
    {
        private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "whisper-v09-" + Guid.NewGuid().ToString("N"));
        public RecordingRepository Repository { get; }
        public FakeCredentialVault Vault { get; } = new();
        public ApiConfigurationStore Configuration { get; }
        public RecordingManagement Management { get; }
        public RecordingManagementViewModel ViewModel { get; }
        public Guid Id { get; }
        public HttpTap Http { get; } = new();
        public UploadCoordinator Upload { get; }
        public JobTracker Tracker { get; }
        private readonly TrackingTransport trackingTransport;
        private readonly UploadTransport uploadTransport;

        public ManagementRig()
        {
            Repository = new(Path.Combine(Root, "recordings"));
            Configuration = new(Path.Combine(Root, "settings.json"));
            Configuration.Save(new Uri("https://api.example/v1"));
            Vault.SaveAsync("test-key").GetAwaiter().GetResult();
            Id = AddRecording(RecordingState.Transcribed);
            Management = new(Repository, Vault, Root);
            ViewModel = new(Management);
            trackingTransport = new(Http);
            uploadTransport = new(Http);
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
            Upload = new(Repository, Configuration, Vault, uploadTransport, clock);
            Tracker = new(Repository, Configuration, Vault, trackingTransport, uploadTransport, new Connected(), clock,
                new FakeScheduler(), new FakeNotifier());
        }

        public Guid AddRecording(RecordingState state)
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(Repository.DirectoryFor(id));
            Write(new RecordingMetadata(id, "Reunião", DateTimeOffset.Parse("2026-10-20T12:34:56-03:00"), state,
                TimeSpan.FromMinutes(30), [], [], JobId: "job-1"));
            File.WriteAllBytes(Repository.AudioPath(id), [1, 2, 3]);
            File.WriteAllText(Path.Combine(Repository.DirectoryFor(id), "transcription.txt"), "Texto local");
            File.WriteAllText(Path.Combine(Repository.DirectoryFor(id), "result-v1.json"), "{\"schemaVersion\":1,\"segments\":[]}");
            return id;
        }

        private void Write(RecordingMetadata metadata) => File.WriteAllText(Path.Combine(Repository.DirectoryFor(metadata.Id), "metadata.json"), JsonSerializer.Serialize(metadata, Options));
        public void SetState(RecordingState state) => Write(Repository.Read(Id) with { State = state });
        public Dictionary<string, byte[]> Contents() => Directory.EnumerateFiles(Repository.DirectoryFor(Id))
            .Where(path => !path.EndsWith("metadata.json", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        public void AssertContents(Dictionary<string, byte[]> contents)
        {
            foreach (var (path, bytes) in contents) Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        public void Dispose()
        {
            Http.Continue.TrySetResult();
            trackingTransport.Dispose();
            uploadTransport.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
