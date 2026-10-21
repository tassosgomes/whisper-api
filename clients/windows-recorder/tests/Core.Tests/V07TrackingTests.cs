using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tests.Doubles;
using Whisper.Recorder.Core.Tests.Fixtures;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Uploading;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V07TrackingTests(V06ApiFixture api) : IClassFixture<V06ApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealUploadProcessingCompletionSavesExactJsonAndTimestampedTextAndNotifiesOnce()
    {
        await using var rig = await TrackingRig.Create(api);
        var events = new List<TrackingStatus>();
        rig.Tracker.Changed += events.Add;
        await rig.Job("processing");
        await rig.Poll();
        Assert.Equal("processing", rig.Metadata.PublicJobStatus);
        Assert.Contains(events, s => s.Recording.PublicJobStatus == "processing");
        await rig.Complete();
        await rig.Next();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.Equal("completed", rig.Metadata.PublicJobStatus);
        Assert.True(rig.Tap.ResultBytes!.AsSpan().SequenceEqual(File.ReadAllBytes(rig.JsonPath)));
        Assert.Equal(new[] { "[00:00:01.240] Olá reunião", "[01:01:01.007] Outro trecho" }, File.ReadAllLines(rig.TextPath));
        Assert.Equal(RecordingState.Transcribed, events[^1].Recording.State);
        Assert.Equal(new[] { RecorderNotification.TranscriptionCompleted }, rig.Notifier.Notifications);
        var calls = rig.Tap.Calls;
        await rig.Poll();
        Assert.Equal(calls, rig.Tap.Calls);
        rig.AssertPreserved();
    }

    [Fact]
    public async Task ReopeningProcessingResumesWithoutReuploadOrResettingDueTime()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Job("processing");
        await rig.Poll();
        var next = rig.Metadata.NextPollAt;
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(1, rig.Tap.Calls);
        Assert.Equal(next, rig.Metadata.NextPollAt);
        await rig.Complete();
        await rig.Next();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
    }

    [Fact]
    public async Task PollingGrowsToThirtySecondsAndStopsAtTerminal()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Poll();
        Assert.Equal("queued", rig.Metadata.PublicJobStatus);
        Assert.Contains("fila", rig.Tracker.Status(rig.Id).Message);
        var delays = new List<int> { rig.Metadata.PollDelaySeconds };
        for (var i = 0; i < 6; i++) { await rig.Next(); delays.Add(rig.Metadata.PollDelaySeconds); }
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30 }, delays);
        Assert.Equal(7, rig.Tap.Calls);
        await rig.Complete();
        await rig.Next();
        rig.Clock.UtcNow += TimeSpan.FromDays(2);
        await rig.Poll();
        Assert.Equal(9, rig.Tap.Calls);
    }

    [Fact]
    public async Task ServerErrorsBackOffToSixtySecondsWithoutFailureOrHiddenRetries()
    {
        await using var rig = await TrackingRig.Create(api);
        rig.Tap.Before = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable));
        var delays = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            await rig.Next();
            delays.Add(rig.Metadata.PollDelaySeconds);
            Assert.Equal(i + 1, rig.Tap.Calls);
            Assert.Equal(RecordingState.Processing, rig.Metadata.State);
            Assert.Equal(TrackingProblem.Service, rig.Metadata.TrackingProblem);
        }
        Assert.Equal(new[] { 10, 20, 40, 60, 60 }, delays);
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(5, rig.Tap.Calls);
        rig.Tap.Before = null;
        await rig.Complete();
        await rig.Next();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
    }

    [Fact]
    public async Task OfflineDoesNotContactApiOrMarkFailureAndResumesOnConnection()
    {
        await using var rig = await TrackingRig.Create(api);
        var events = new List<TrackingStatus>();
        rig.Tracker.Changed += events.Add;
        rig.Connectivity.IsConnected = false;
        await rig.Poll();
        await rig.Poll();
        Assert.Equal(0, rig.Tap.Calls);
        Assert.True(Assert.Single(events).IsOffline);
        Assert.Contains("Sem conexão", rig.Tracker.Status(rig.Id).Message);
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        Assert.Null(rig.Metadata.NextPollAt);
        rig.Connectivity.IsConnected = true;
        await rig.Poll();
        Assert.Equal(1, rig.Tap.Calls);
        Assert.False(events[^1].IsOffline);
    }

    [Fact]
    public async Task NetworkExceptionShowsOfflineWithBackoffAndRecovers()
    {
        await using var rig = await TrackingRig.Create(api);
        rig.Tap.Before = (_, _) => throw new HttpRequestException(rig.Key);
        await rig.Poll();
        Assert.True(rig.Tracker.Status(rig.Id).IsOffline);
        Assert.Equal(10, rig.Metadata.PollDelaySeconds);
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        Assert.Empty(rig.Notifier.Notifications);
        rig.Tap.Before = null;
        await rig.Next();
        Assert.False(rig.Tracker.Status(rig.Id).IsOffline);
        rig.AssertPreserved();
    }

    [Fact]
    public async Task RealFailedJobKeepsCodeExplainsCauseNotifiesAndManualResendCreatesNewJob()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Job("failed", "UNSUPPORTED_MEDIA_FORMAT");
        await rig.Poll();
        Assert.Equal(RecordingState.Failed, rig.Metadata.State);
        Assert.Equal("UNSUPPORTED_MEDIA_FORMAT", rig.Metadata.FailureCode);
        Assert.True(rig.Tracker.Status(rig.Id).CanResend);
        Assert.Contains("formato", rig.Tracker.Status(rig.Id).Message);
        Assert.Equal(new[] { RecorderNotification.TranscriptionFailed }, rig.Notifier.Notifications);
        Assert.False(File.Exists(rig.JsonPath));
        var firstJob = rig.Metadata.JobId;
        Assert.True((await rig.Send()).Succeeded);
        Assert.NotEqual(firstJob, rig.Metadata.JobId);
        Assert.Equal(2, rig.Metadata.UploadAttempt);
        Assert.Null(rig.Metadata.FailureCode);
        await rig.Complete();
        await rig.Poll();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        rig.AssertPreserved();
    }

    [Fact]
    public async Task RealRetention404IsResultExpiredAndResendUsesNewAttempt()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Job("completed", expired: true);
        await rig.Poll();
        Assert.Equal(RecordingState.ResultExpired, rig.Metadata.State);
        Assert.Contains("gravação está no PC", rig.Tracker.Status(rig.Id).Message);
        Assert.True(rig.Tracker.Status(rig.Id).CanResend);
        Assert.Contains("result-expired", File.ReadAllText(rig.MetadataPath));
        var firstJob = rig.Metadata.JobId;
        var calls = rig.Tap.Calls;
        await rig.Poll();
        Assert.Equal(calls, rig.Tap.Calls);
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(2, rig.Metadata.UploadAttempt);
        Assert.NotEqual(firstJob, rig.Metadata.JobId);
    }

    [Fact]
    public async Task ExpirationBetweenStatusAndResultAlsoBecomesResultExpired()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Complete();
        rig.Tap.Before = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal))
                await rig.Job("completed", expired: true);
            return null;
        };
        await rig.Poll();
        Assert.Equal(RecordingState.ResultExpired, rig.Metadata.State);
        Assert.Equal(new[] { RecorderNotification.TranscriptionFailed }, rig.Notifier.Notifications);
    }

    [Fact]
    public async Task UnknownFutureSchemaIsKeptByteForByteAndRequestsUpdateWithoutRepolling()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Complete();
        var raw = Encoding.UTF8.GetBytes("{\n  \"schemaVersion\": 99, \"future\": {\"content\":\"preservar\"}\n}");
        rig.Tap.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) } : null);
        await rig.Poll();
        Assert.Equal(raw, File.ReadAllBytes(rig.JsonPath));
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.Equal(TrackingProblem.UpdateRequired, rig.Metadata.TrackingProblem);
        Assert.Contains("Atualize", rig.Tracker.Status(rig.Id).Message);
        Assert.False(File.Exists(rig.TextPath));
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(2, rig.Tap.Calls);
        Assert.Equal(TrackingProblem.UpdateRequired, rig.Metadata.TrackingProblem);
    }

    [Fact]
    public async Task EmptyRealResultIsSavedWithEmptyTextAndNoSpeechMessage()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Job("completed");
        await rig.Poll();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.Equal(TrackingProblem.EmptyTranscript, rig.Metadata.TrackingProblem);
        Assert.Equal("", File.ReadAllText(rig.TextPath));
        Assert.True(File.Exists(rig.JsonPath));
        Assert.Contains("Nenhuma fala reconhecida", rig.Tracker.Status(rig.Id).Message);
    }

    [Fact]
    public async Task LostCompleteResponseInUploadingRecoversAcceptedJobWithoutSendingAudio()
    {
        await using var rig = await TrackingRig.Create(api);
        var jobId = rig.Metadata.JobId;
        rig.Update(m => m with { State = RecordingState.Uploading, JobId = null });
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(jobId, rig.Metadata.JobId);
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        using var evidence = await api.CommandAsync(new { operation = "evidence", reference = rig.Id.ToString("D") });
        Assert.Equal(1, evidence.RootElement.GetProperty("jobs").GetInt32());
        var requests = evidence.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(2, requests.Count(r => r.GetProperty("path").GetString()!.EndsWith("/complete", StringComparison.Ordinal)));
        Assert.Single(requests, r => r.GetProperty("method").GetString() == "POST" && r.GetProperty("path").GetString() == "/v1/transcription-uploads");
    }

    [Fact]
    public async Task UploadWithoutCompletedPutNeedsManualActionAndNeverSendsAudioOnReopen()
    {
        await using var rig = await TrackingRig.Create(api);
        rig.Update(m => m with { State = RecordingState.Uploading, JobId = null, PutCompleted = false });
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(0, rig.Tap.Calls);
        Assert.Equal(TrackingProblem.SendRequired, rig.Metadata.TrackingProblem);
        Assert.True(rig.Tracker.Status(rig.Id).CanResend);
        rig.AssertPreserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrRejectedKeyPausesWithoutLosingJob(bool rejected)
    {
        await using var rig = await TrackingRig.Create(api);
        if (rejected) await rig.Vault.SaveAsync("test-invalid-key", Token);
        else await rig.Vault.RemoveAsync(Token);
        await rig.Poll();
        Assert.Equal(TrackingProblem.ConfigurationRequired, rig.Metadata.TrackingProblem);
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        Assert.Equal(rejected ? 1 : 0, rig.Tap.Calls);
        await rig.Vault.SaveAsync(rig.Key, Token);
        await rig.Complete();
        await rig.Next();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
    }

    [Fact]
    public async Task LocalWriteFailureNeverMarksTranscribedAndKeepsAudioForRetry()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Complete();
        Directory.CreateDirectory(rig.TextPath);
        await rig.Poll();
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        Assert.Equal(TrackingProblem.Storage, rig.Metadata.TrackingProblem);
        Assert.True(File.Exists(rig.JsonPath));
        Assert.Empty(rig.Notifier.Notifications);
        Directory.Delete(rig.TextPath);
        await rig.Next();
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.Equal(new[] { RecorderNotification.TranscriptionCompleted }, rig.Notifier.Notifications);
    }

    [Fact]
    public async Task CompletedLocalCopySurvivesServiceExpiryAndOfflineRestart()
    {
        await using var rig = await TrackingRig.Create(api);
        await rig.Complete();
        await rig.Poll();
        var json = File.ReadAllBytes(rig.JsonPath);
        var text = File.ReadAllText(rig.TextPath);
        await rig.Job("completed", expired: true);
        rig.Connectivity.IsConnected = false;
        rig.Clock.UtcNow += TimeSpan.FromDays(3);
        rig.Reopen();
        await rig.Poll();
        Assert.Equal(json, File.ReadAllBytes(rig.JsonPath));
        Assert.Equal(text, File.ReadAllText(rig.TextPath));
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        Assert.False(rig.Tracker.Status(rig.Id).IsOffline);
        Assert.Equal(2, rig.Tap.Calls);
    }

    [Fact]
    public async Task RunUsesInjectedSchedulerFollowsNewUploadsAndHonorsCancellation()
    {
        await using var rig = await TrackingRig.Create(api, send: false);
        var scheduler = new ControlledScheduler();
        rig.Reopen(scheduler);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = rig.Tracker.RunAsync(stop.Token);
        await scheduler.WaitForNextDelay(Token);
        Assert.Equal(0, rig.Tap.Calls);
        Assert.True((await rig.Send()).Succeeded);
        await rig.Complete();
        scheduler.Pulse();
        await scheduler.WaitForNextDelay(Token);
        Assert.Equal(RecordingState.Transcribed, rig.Metadata.State);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(2, rig.Tap.Calls);
    }

    [Fact]
    public async Task MultipleJobsProgressIndependentlyWhenOneApiCallFails()
    {
        await using var first = await TrackingRig.Create(api);
        // Both records belong to the same configured key, as in the actual app.
        var secondId = Guid.NewGuid();
        var secondDirectory = first.Repository.DirectoryFor(secondId);
        Directory.CreateDirectory(secondDirectory);
        File.Copy(first.Repository.AudioPath(first.Id), first.Repository.AudioPath(secondId));
        File.WriteAllText(Path.Combine(secondDirectory, "metadata.json"), JsonSerializer.Serialize(
            first.Metadata with { Id = secondId, State = RecordingState.Saved, JobId = null, UploadId = null, UploadAttempt = 0 }, TrackingRig.Options));
        Assert.True((await first.Upload.ConfirmAndSendAsync(first.Upload.Prepare(secondId), cancellationToken: Token)).Succeeded);
        var secondJob = first.Repository.Read(secondId).JobId;
        using var completed = await api.CommandAsync(new { operation = "job_state", jobId = secondJob, status = "completed" });
        first.Tap.Before = (request, _) => request.RequestUri!.AbsolutePath.Contains(first.Metadata.JobId!, StringComparison.Ordinal)
            ? throw new HttpRequestException() : Task.FromResult<HttpResponseMessage?>(null);
        await first.Poll();
        Assert.Equal(TrackingProblem.Network, first.Metadata.TrackingProblem);
        Assert.Equal(RecordingState.Transcribed, first.Repository.Read(secondId).State);
    }

    private sealed class Connectivity : IConnectivity
    {
        public bool IsConnected { get; set; } = true;
    }

    private sealed class HttpTap : DelegatingHandler
    {
        public HttpTap() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        public int Calls { get; private set; }
        public byte[]? ResultBytes { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Before { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Calls++;
            var response = (Before is null ? null : await Before(request, cancellationToken)) ?? await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/result", StringComparison.Ordinal))
                ResultBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return response;
        }
    }

    private sealed class TrackingRig : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        private readonly V06ApiFixture api;
        private readonly RecordingRig recording;
        private readonly UploadTransport uploadTransport = new();
        private readonly TrackingTransport trackingTransport;
        private readonly string directory;
        private byte[] audio = [];
        public string Key { get; }
        public Guid Id => recording.Session.Current!.Id;
        public RecordingRepository Repository => recording.Repository;
        public FakeClock Clock { get; } = new() { UtcNow = DateTimeOffset.UtcNow };
        public FakeCredentialVault Vault { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public Connectivity Connectivity { get; } = new();
        public HttpTap Tap { get; } = new();
        public ApiConfigurationStore Configuration { get; }
        public UploadCoordinator Upload { get; private set; } = null!;
        public JobTracker Tracker { get; private set; } = null!;
        public RecordingMetadata Metadata => Repository.Read(Id);
        public string JsonPath => Path.Combine(Repository.DirectoryFor(Id), "result-v1.json");
        public string TextPath => Path.Combine(Repository.DirectoryFor(Id), "transcription.txt");
        public string MetadataPath => Path.Combine(Repository.DirectoryFor(Id), "metadata.json");

        private TrackingRig(V06ApiFixture api, string key)
        {
            this.api = api;
            Key = key;
            directory = Path.Combine(Path.GetTempPath(), "whisper-v07-" + Guid.NewGuid());
            recording = new RecordingRig(Path.Combine(directory, "recordings"));
            Configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
            Configuration.Save(new Uri(api.Endpoint));
            trackingTransport = new TrackingTransport(Tap);
            Reopen();
        }

        public static async Task<TrackingRig> Create(V06ApiFixture api, bool send = true)
        {
            using var credential = await api.CommandAsync(new { operation = "credential" });
            var rig = new TrackingRig(api, credential.RootElement.GetProperty("key").GetString()!);
            await rig.Vault.SaveAsync(rig.Key, Token);
            await rig.recording.Start();
            rig.recording.Emit(AudioSourceKind.Microphone, 1000);
            rig.recording.Advance(1);
            await rig.recording.Session.StopAsync(Token);
            rig.audio = File.ReadAllBytes(rig.Repository.AudioPath(rig.Id));
            if (send) Assert.True((await rig.Send()).Succeeded);
            return rig;
        }

        public void Reopen(IScheduler? scheduler = null)
        {
            var repository = new RecordingRepository(Repository.RootDirectory);
            var configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
            Upload = new UploadCoordinator(repository, configuration, Vault, uploadTransport, Clock);
            Tracker = new JobTracker(repository, configuration, Vault, trackingTransport, uploadTransport,
                Connectivity, Clock, scheduler ?? new FakeScheduler(), Notifier);
        }
        public Task Poll() => Tracker.PollOnceAsync(Token);
        public async Task Next()
        {
            if (Metadata.NextPollAt is { } next) Clock.UtcNow = next;
            await Poll();
        }
        public Task<UploadResult> Send() => Upload.ConfirmAndSendAsync(Upload.Prepare(Id), cancellationToken: Token);
        public async Task Job(string status, string? failureCode = null, bool expired = false)
        {
            using var result = await api.CommandAsync(new { operation = "job_state", jobId = Metadata.JobId, status, failureCode, expired });
        }
        public async Task Complete()
        {
            using var result = await api.CommandAsync(new
            {
                operation = "job_state",
                jobId = Metadata.JobId,
                status = "completed",
                segments = new[] { new { startMs = 1240, endMs = 4100, text = " Olá reunião " }, new { startMs = 3661007, endMs = 3662000, text = "Outro trecho" } }
            });
        }
        public void Update(Func<RecordingMetadata, RecordingMetadata> update) => File.WriteAllText(MetadataPath, JsonSerializer.Serialize(update(Metadata), Options));
        public void AssertPreserved()
        {
            Assert.Equal(audio, File.ReadAllBytes(Repository.AudioPath(Id)));
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("audio.wav", StringComparison.Ordinal)))
                Assert.DoesNotContain(Key, File.ReadAllText(path));
        }
        public async ValueTask DisposeAsync()
        {
            AssertPreserved();
            trackingTransport.Dispose();
            uploadTransport.Dispose();
            await recording.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
