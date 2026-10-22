using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tests.Doubles;
using Whisper.Recorder.Core.Tests.Fixtures;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Presentation.Uploading;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V06UploadTests(V06ApiFixture api) : IClassFixture<V06ApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConfirmedUploadUsesRealApiSignedHeadersProgressAndDurableJob()
    {
        await using var rig = await UploadRig.Create(api);
        var confirmation = rig.Coordinator.Prepare(rig.Id);
        Assert.Equal(rig.Metadata.Title, confirmation.Title);
        Assert.Equal(rig.Metadata.Duration, confirmation.Duration);
        Assert.Equal(rig.Audio.Length, confirmation.SizeBytes);
        var states = new List<RecordingState>();
        rig.Coordinator.Changed += () => states.Add(rig.Metadata.State);
        var progress = new List<UploadProgress>();
        Assert.True((await rig.Send(progress: new ProgressSink(progress.Add))).Succeeded);
        Assert.Contains(RecordingState.Uploading, states);
        Assert.Equal(RecordingState.Processing, states[^1]);
        Assert.Equal(RecordingState.Processing, rig.Metadata.State);
        Assert.NotNull(rig.Metadata.UploadId);
        Assert.NotNull(rig.Metadata.JobId);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        Assert.Equal(new UploadProgress(rig.Audio.Length, rig.Audio.Length), progress[^1]);
        Assert.Equal(0, progress[0].SentBytes);
        Assert.True(progress.Zip(progress.Skip(1)).All(pair => pair.First.SentBytes <= pair.Second.SentBytes));
        Assert.Equal("audio/wav", rig.Storage.PutHeaders["Content-Type"]);
        Assert.Equal(rig.Audio.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), rig.Storage.PutHeaders["Content-Length"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(rig.Audio)), rig.Storage.PutHeaders["x-amz-checksum-sha256"]);
        Assert.False(rig.Storage.PutHeaders.ContainsKey("X-API-Key"));
        using var evidence = await rig.Evidence();
        Assert.Equal(1, evidence.RootElement.GetProperty("jobs").GetInt32());
        var upload = Assert.Single(evidence.RootElement.GetProperty("uploads").EnumerateArray());
        Assert.Equal(rig.Metadata.UploadId, upload.GetProperty("uploadId").GetString());
        Assert.Equal(rig.Metadata.JobId, upload.GetProperty("jobId").GetString());
        var create = Assert.Single(Creates(evidence));
        var payload = create.GetProperty("body");
        Assert.Equal(rig.Id.ToString("D"), payload.GetProperty("clientReference").GetString());
        Assert.Equal(rig.Audio.Length, payload.GetProperty("sizeBytes").GetInt64());
        Assert.Equal((long)confirmation.Duration.TotalMilliseconds, payload.GetProperty("durationMs").GetInt64());
        Assert.Equal(rig.Storage.PutHeaders["x-amz-checksum-sha256"], payload.GetProperty("checksumSha256").GetString());
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task InterruptedRecordingCanBeSentWithoutChangingItsAudio()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Update(m => m with { State = RecordingState.Interrupted });
        Assert.True((await rig.Send()).Succeeded);
        rig.AssertIntactAndSecretFree();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingConfigurationOrKeyNavigatesToSettingsWithoutHttp(bool addressOnly)
    {
        await using var rig = await UploadRig.Create(api, configured: false);
        if (addressOnly) rig.Configuration.Save(new Uri(api.Endpoint));
        var viewModel = new UploadViewModel(rig.Coordinator);
        var navigation = 0;
        viewModel.ConfigurationRequested += () => navigation++;
        viewModel.Prepare(rig.Id);
        await viewModel.ConfirmAndSendAsync(cancellationToken: Token);
        Assert.Equal(UploadProblem.ConfigurationRequired, viewModel.Result!.Problem);
        Assert.Equal(1, navigation);
        Assert.Equal(0, rig.Api.Calls);
        Assert.Equal(0, rig.Storage.Calls);
        Assert.Equal(0, rig.Metadata.UploadAttempt);
        Assert.Equal(RecordingState.Saved, rig.Metadata.State);
    }

    [Fact]
    public async Task RecordingAndPreparingConfirmationNeverSendAutomatically()
    {
        await using var rig = await UploadRig.Create(api);
        var viewModel = new UploadViewModel(rig.Coordinator);
        await viewModel.ConfirmAndSendAsync(cancellationToken: Token);
        viewModel.Prepare(rig.Id);
        var reopened = rig.Reopen();
        Assert.NotNull(reopened.Prepare(rig.Id));
        Assert.Equal(0, rig.Api.Calls);
        Assert.Equal(0, rig.Storage.Calls);
        Assert.Equal(RecordingState.Saved, rig.Metadata.State);
    }

    [Fact]
    public async Task ChangedConfirmationCannotSendUntilUserSeesCurrentFacts()
    {
        await using var rig = await UploadRig.Create(api);
        var confirmation = rig.Coordinator.Prepare(rig.Id);
        rig.Update(m => m with { Title = "Outro título" });
        var result = await rig.Coordinator.ConfirmAndSendAsync(confirmation, cancellationToken: Token);
        Assert.Equal(UploadProblem.ConfirmationRequired, result.Problem);
        Assert.Equal(0, rig.Api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFailureIsManualAndReusesSameAttemptEvenWhenResponseWasLost(bool afterResponse)
    {
        await using var rig = await UploadRig.Create(api);
        if (afterResponse) rig.Api.After = (_, _) => throw new HttpRequestException(rig.Key);
        else rig.Api.Before = (_, _) => throw new HttpRequestException(rig.Key);
        var result = await rig.Send();
        Assert.Equal(UploadProblem.Network, result.Problem);
        Assert.Equal(1, rig.Api.Calls);
        Assert.Equal(0, rig.Storage.Calls);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        rig.Api.ClearFaults();
        Assert.True((await rig.Send()).Succeeded);
        using var evidence = await rig.Evidence();
        Assert.Single(evidence.RootElement.GetProperty("uploads").EnumerateArray());
        Assert.Equal(1, evidence.RootElement.GetProperty("jobs").GetInt32());
        Assert.Single(rig.Api.IdempotencyKeys.Distinct());
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task FailedPutDoesNotLoopAndNextManualActionSendsWholeFile()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.Before = (_, _) => throw new HttpRequestException("network unavailable");
        Assert.Equal(UploadProblem.Network, (await rig.Send()).Problem);
        Assert.Equal(1, rig.Api.Calls);
        Assert.Equal(1, rig.Storage.Calls);
        Assert.False(rig.Metadata.PutCompleted);
        var uploadId = rig.Metadata.UploadId;
        rig.Storage.ClearFaults();
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(uploadId, rig.Metadata.UploadId);
        Assert.Equal(2, rig.Storage.Calls);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task FailureBetweenPutAndCompleteResumesAfterReopeningWithoutAnotherPut()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Api.Before = (request, _) => IsComplete(request) ? throw new HttpRequestException() : Task.FromResult<HttpResponseMessage?>(null);
        Assert.Equal(UploadProblem.Network, (await rig.Send()).Problem);
        Assert.True(rig.Metadata.PutCompleted);
        Assert.Null(rig.Metadata.JobId);
        Assert.Equal(1, rig.Storage.Calls);
        rig.Api.ClearFaults();
        var reopened = rig.Reopen();
        Assert.True((await reopened.ConfirmAndSendAsync(reopened.Prepare(rig.Id), cancellationToken: Token)).Succeeded);
        Assert.Equal(1, rig.Storage.Calls);
        using var evidence = await rig.Evidence();
        Assert.Equal(1, evidence.RootElement.GetProperty("jobs").GetInt32());
        Assert.Single(evidence.RootElement.GetProperty("uploads").EnumerateArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task LostCompleteResponseRecoversSameJobEvenHoursLater(int hours)
    {
        await using var rig = await UploadRig.Create(api);
        rig.Api.After = (request, _) => IsComplete(request) ? throw new HttpRequestException() : Task.CompletedTask;
        Assert.Equal(UploadProblem.Network, (await rig.Send()).Problem);
        Assert.True(rig.Metadata.PutCompleted);
        Assert.Null(rig.Metadata.JobId);
        using var before = await rig.Evidence();
        var job = Assert.Single(before.RootElement.GetProperty("uploads").EnumerateArray()).GetProperty("jobId").GetString();
        using var advanced = await api.CommandAsync(new { operation = "advance", hours });
        rig.Clock.UtcNow += TimeSpan.FromHours(hours);
        rig.Api.ClearFaults();
        var reopened = rig.Reopen();
        Assert.True((await reopened.ConfirmAndSendAsync(reopened.Prepare(rig.Id), cancellationToken: Token)).Succeeded);
        Assert.Equal(job, rig.Metadata.JobId);
        Assert.Equal(1, rig.Storage.Calls);
        Assert.Equal(1, rig.Api.Completes);
        Assert.Single(rig.Api.IdempotencyKeys.Distinct());
        using var after = await rig.Evidence();
        Assert.Equal(1, after.RootElement.GetProperty("jobs").GetInt32());
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task ExpiredSignedUrlIsRenewedOnceWithTheSameKey()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.Before = async (_, _) =>
        {
            if (rig.Storage.Calls != 1) return null;
            using var advanced = await api.CommandAsync(new { operation = "advance", hours = 2 });
            rig.Clock.UtcNow += TimeSpan.FromHours(2);
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        };
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(2, rig.Storage.Calls);
        Assert.Equal(2, rig.Api.IdempotencyKeys.Count);
        Assert.Single(rig.Api.IdempotencyKeys.Distinct());
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        using var evidence = await rig.Evidence();
        var creates = Creates(evidence);
        Assert.Equal(new[] { 201, 200 }, creates.Select(c => c.GetProperty("status").GetInt32()).ToArray());
        Assert.Single(evidence.RootElement.GetProperty("uploads").EnumerateArray());
    }

    [Fact]
    public async Task PersistentlyForbiddenStorageStopsAfterOneRenewal()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.Before = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
        Assert.Equal(UploadProblem.Network, (await rig.Send()).Problem);
        Assert.Equal(2, rig.Storage.Calls);
        Assert.Equal(2, rig.Api.Calls);
        Assert.Equal(0, rig.Api.Completes);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
    }

    [Fact]
    public async Task ChecksumRefusalPreservesRecordingAndNextManualAttemptUsesNewKey()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.After = async (_, _) =>
        {
            using var corrupted = await api.CommandAsync(new { operation = "corrupt_checksum", uploadId = rig.Metadata.UploadId });
        };
        var result = await rig.Send();
        Assert.Equal(UploadProblem.ChecksumMismatch, result.Problem);
        Assert.Contains("conteúdo", result.Message);
        Assert.True(rig.Metadata.UploadRejected);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        var firstUpload = rig.Metadata.UploadId;
        rig.Storage.ClearFaults();
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(2, rig.Metadata.UploadAttempt);
        Assert.NotEqual(firstUpload, rig.Metadata.UploadId);
        Assert.Equal(2, rig.Api.IdempotencyKeys.Distinct().Count());
        using var evidence = await rig.Evidence();
        Assert.Equal(1, evidence.RootElement.GetProperty("jobs").GetInt32());
        Assert.Equal(2, evidence.RootElement.GetProperty("uploads").GetArrayLength());
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task UploadRejectedOnReplayOnlyIncrementsAttemptOnFollowingManualAction()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.Before = (_, _) => throw new HttpRequestException();
        await rig.Send();
        using var rejected = await api.CommandAsync(new { operation = "reject", uploadId = rig.Metadata.UploadId });
        rig.Storage.ClearFaults();
        Assert.Equal(UploadProblem.Rejected, (await rig.Send()).Problem);
        Assert.True(rig.Metadata.UploadRejected);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        Assert.Equal(1, rig.Storage.Calls);
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(2, rig.Metadata.UploadAttempt);
    }

    [Fact]
    public async Task RealUnauthorizedResponseOpensSettingsWithoutPutOrNewAttempt()
    {
        await using var rig = await UploadRig.Create(api);
        await rig.Vault.SaveAsync("wsk_00000000000000000000000000000000.invalid", Token);
        var viewModel = new UploadViewModel(rig.Coordinator);
        var navigation = 0;
        viewModel.ConfigurationRequested += () => navigation++;
        viewModel.Prepare(rig.Id);
        await viewModel.ConfirmAndSendAsync(cancellationToken: Token);
        Assert.Equal(UploadProblem.KeyRejected, viewModel.Result!.Problem);
        Assert.Contains("Atualize a chave", viewModel.Message);
        Assert.Equal(1, navigation);
        Assert.Equal(0, rig.Storage.Calls);
        Assert.False(rig.Metadata.UploadRejected);
        await rig.Vault.SaveAsync(rig.Key, Token);
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        rig.AssertIntactAndSecretFree();
    }

    [Theory]
    [InlineData("sizeBytes", "5368709121", UploadProblem.SizeLimit, "5 GiB")]
    [InlineData("durationMs", "7200001", UploadProblem.DurationLimit, "2 horas")]
    [InlineData("contentType", "\"audio/ogg\"", UploadProblem.UnsupportedFormat, "formato")]
    public async Task RealAdmissionLimitsProduceSafeMessagesWithoutDiscardingAudio(string field, string value, UploadProblem expected, string message)
    {
        await using var rig = await UploadRig.Create(api);
        rig.Api.Before = async (request, token) =>
        {
            if (IsComplete(request)) return null;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            body[field] = JsonNode.Parse(value);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return null;
        };
        var result = await rig.Send();
        Assert.Equal(expected, result.Problem);
        Assert.Contains(message, result.Message);
        Assert.Equal(0, rig.Storage.Calls);
        Assert.False(rig.Metadata.UploadRejected);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task Real429HonorsRetryAfterAcrossReopenAndNeverAutomaticallyResends()
    {
        await using var rig = await UploadRig.Create(api);
        using var limit = await api.CommandAsync(new { operation = "limit", value = 1 });
        // Leave a pending upload for the same credential using the actual API.
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, api.Endpoint + "/v1/transcription-uploads");
        request.Headers.Add("X-API-Key", rig.Key);
        request.Headers.Add("Idempotency-Key", "pending-for-rate-limit");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            contentType = "audio/wav",
            sizeBytes = rig.Audio.Length,
            checksumSha256 = Convert.ToBase64String(SHA256.HashData(rig.Audio))
        }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var start = rig.Clock.UtcNow;
        var result = await rig.Send();
        Assert.Equal(UploadProblem.RetryLater, result.Problem);
        Assert.Equal(start.AddSeconds(60), result.RetryAt);
        Assert.Equal(result.RetryAt, rig.Metadata.UploadRetryAt);
        var calls = rig.Api.Calls;
        var reopened = rig.Reopen();
        Assert.Equal(UploadProblem.RetryLater, (await reopened.ConfirmAndSendAsync(reopened.Prepare(rig.Id), cancellationToken: Token)).Problem);
        Assert.Equal(calls, rig.Api.Calls);
        rig.Clock.UtcNow += TimeSpan.FromSeconds(61);
        using var released = await api.CommandAsync(new { operation = "limit", value = 1000 });
        Assert.Equal(calls, rig.Api.Calls);
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
    }

    [Fact]
    public async Task ExistingTranscriptionRequiresExtraConfirmationAndGetsNewAttempt()
    {
        await using var rig = await UploadRig.Create(api);
        Assert.True((await rig.Send()).Succeeded);
        var firstJob = rig.Metadata.JobId;
        rig.Update(m => m with { State = RecordingState.Transcribed });
        var confirmation = rig.Coordinator.Prepare(rig.Id);
        Assert.True(confirmation.HasTranscription);
        var calls = rig.Api.Calls;
        Assert.Equal(UploadProblem.ConfirmationRequired,
            (await rig.Coordinator.ConfirmAndSendAsync(confirmation, cancellationToken: Token)).Problem);
        Assert.Equal(calls, rig.Api.Calls);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        Assert.True((await rig.Coordinator.ConfirmAndSendAsync(confirmation, existingTranscriptionConfirmed: true, cancellationToken: Token)).Succeeded);
        Assert.Equal(2, rig.Metadata.UploadAttempt);
        Assert.NotEqual(firstJob, rig.Metadata.JobId);
        using var evidence = await rig.Evidence();
        Assert.Equal(2, evidence.RootElement.GetProperty("jobs").GetInt32());
        rig.AssertIntactAndSecretFree();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResendAfterNetworkFailureRetriesSameAttemptAndKeyWithoutNewConfirmation(bool failOnCreate)
    {
        await using var rig = await UploadRig.Create(api);
        Assert.True((await rig.Send()).Succeeded);
        rig.Update(m => m with { State = RecordingState.Transcribed });
        var confirmation = rig.Coordinator.Prepare(rig.Id);
        Assert.True(confirmation.HasTranscription);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>> fault = (_, _) => throw new HttpRequestException("network unavailable");
        if (failOnCreate) rig.Api.Before = fault;
        else rig.Storage.Before = fault;
        Assert.Equal(UploadProblem.Network,
            (await rig.Coordinator.ConfirmAndSendAsync(confirmation, existingTranscriptionConfirmed: true, cancellationToken: Token)).Problem);
        Assert.Equal((RecordingState.Uploading, 2), (rig.Metadata.State, rig.Metadata.UploadAttempt));
        rig.Api.ClearFaults();
        rig.Storage.ClearFaults();

        // Mesma confirmação (ainda com "tem transcrição"): continua a tentativa 2, sem pedir confirmação de novo.
        var result = await rig.Coordinator.ConfirmAndSendAsync(confirmation, existingTranscriptionConfirmed: true, cancellationToken: Token);
        Assert.True(result.Succeeded);
        Assert.Equal((RecordingState.Processing, 2), (rig.Metadata.State, rig.Metadata.UploadAttempt));
        Assert.Equal($"recording-{rig.Id:D}-attempt-2", Assert.Single(rig.Api.IdempotencyKeys.Skip(1).Distinct()));
        using var evidence = await rig.Evidence();
        Assert.Equal(2, evidence.RootElement.GetProperty("jobs").GetInt32());
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task ResendRetryAfterFactsChangedAsksForConfirmationAgain()
    {
        await using var rig = await UploadRig.Create(api);
        Assert.True((await rig.Send()).Succeeded);
        rig.Update(m => m with { State = RecordingState.Transcribed });
        var confirmation = rig.Coordinator.Prepare(rig.Id);
        rig.Storage.Before = (_, _) => throw new HttpRequestException("network unavailable");
        Assert.Equal(UploadProblem.Network,
            (await rig.Coordinator.ConfirmAndSendAsync(confirmation, existingTranscriptionConfirmed: true, cancellationToken: Token)).Problem);
        rig.Storage.ClearFaults();
        rig.Update(m => m with { Title = "Outro título" });
        var calls = rig.Api.Calls;
        Assert.Equal(UploadProblem.ConfirmationRequired,
            (await rig.Coordinator.ConfirmAndSendAsync(confirmation, existingTranscriptionConfirmed: true, cancellationToken: Token)).Problem);
        Assert.Equal(calls, rig.Api.Calls);
    }

    [Fact]
    public async Task ProcessingRecordingCannotStartDuplicateJob()
    {
        await using var rig = await UploadRig.Create(api);
        Assert.True((await rig.Send()).Succeeded);
        var calls = rig.Api.Calls;
        Assert.Equal(UploadProblem.AlreadyProcessing, (await rig.Send()).Problem);
        Assert.Equal(calls, rig.Api.Calls);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
    }

    [Fact]
    public async Task CancelledPutPreservesAttemptAndAllowsManualRetry()
    {
        await using var rig = await UploadRig.Create(api);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        rig.Storage.Before = (_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        var result = await rig.Coordinator.ConfirmAndSendAsync(rig.Coordinator.Prepare(rig.Id), cancellationToken: cancellation.Token);
        Assert.Equal(UploadProblem.Cancelled, result.Problem);
        Assert.False(rig.Metadata.PutCompleted);
        rig.Storage.ClearFaults();
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        rig.AssertIntactAndSecretFree();
    }

    [Fact]
    public async Task CompleteNotReceivedMarksPutForNextManualResend()
    {
        await using var rig = await UploadRig.Create(api);
        rig.Storage.Before = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK));
        Assert.Equal(UploadProblem.NotReceived, (await rig.Send()).Problem);
        Assert.False(rig.Metadata.PutCompleted);
        Assert.False(rig.Metadata.UploadRejected);
        rig.Storage.ClearFaults();
        Assert.True((await rig.Send()).Succeeded);
        Assert.Equal(1, rig.Metadata.UploadAttempt);
        Assert.Equal(2, rig.Storage.Calls);
    }

    private static bool IsComplete(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal);
    private static JsonElement[] Creates(JsonDocument evidence) => evidence.RootElement.GetProperty("requests").EnumerateArray()
        .Where(r => r.GetProperty("path").GetString() == "/v1/transcription-uploads").ToArray();

    private sealed class ProgressSink(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }

    private sealed class HttpTap : DelegatingHandler
    {
        public HttpTap() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        public int Calls { get; private set; }
        public int Completes { get; private set; }
        public List<string> IdempotencyKeys { get; } = [];
        public List<string> StorageUrls { get; } = [];
        public Dictionary<string, string> PutHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Before { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage, Task>? After { get; set; }
        public void ClearFaults() { Before = null; After = null; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (IsComplete(request)) Completes++;
            if (request.Headers.TryGetValues("Idempotency-Key", out var keys)) IdempotencyKeys.Add(keys.Single());
            if (request.Method == HttpMethod.Put)
            {
                StorageUrls.Add(request.RequestUri!.AbsoluteUri);
                PutHeaders.Clear();
                foreach (var header in request.Headers.Concat(request.Content!.Headers)) PutHeaders[header.Key] = string.Join(",", header.Value);
            }
            var intercepted = Before is null ? null : await Before(request, cancellationToken);
            var response = intercepted ?? await base.SendAsync(request, cancellationToken);
            try
            {
                if (After is not null) await After(request, response);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private sealed class UploadRig : IAsyncDisposable
    {
        private readonly V06ApiFixture api;
        private readonly RecordingRig recording;
        private readonly UploadTransport transport;
        private readonly StringWriter log = new();
        private readonly DiagnosticLog diagnostic;
        private readonly string directory;
        public string Key { get; }
        public FakeClock Clock { get; } = new() { UtcNow = DateTimeOffset.UtcNow };
        public FakeCredentialVault Vault { get; } = new();
        public ApiConfigurationStore Configuration { get; }
        public RecordingRepository Repository => recording.Repository;
        public Guid Id => recording.Session.Current!.Id;
        public RecordingMetadata Metadata => Repository.Read(Id);
        public byte[] Audio { get; private set; } = [];
        public HttpTap Api { get; } = new();
        public HttpTap Storage { get; } = new();
        public UploadCoordinator Coordinator { get; }

        private UploadRig(V06ApiFixture api, string key)
        {
            this.api = api;
            Key = key;
            directory = Path.Combine(Path.GetTempPath(), "whisper-v06-client-" + Guid.NewGuid());
            recording = new RecordingRig(Path.Combine(directory, "recordings"));
            Configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
            transport = new UploadTransport(Api, Storage);
            diagnostic = new DiagnosticLog(log, Clock);
            Coordinator = Reopen();
        }

        public static async Task<UploadRig> Create(V06ApiFixture api, bool configured = true)
        {
            using var credential = await api.CommandAsync(new { operation = "credential" });
            var rig = new UploadRig(api, credential.RootElement.GetProperty("key").GetString()!);
            await rig.recording.Start();
            rig.recording.Emit(Core.Ports.AudioSourceKind.Microphone, 1000);
            rig.recording.Advance(1);
            await rig.recording.Session.StopAsync(Token);
            rig.Audio = await File.ReadAllBytesAsync(rig.Repository.AudioPath(rig.Id), Token);
            if (configured)
            {
                rig.Configuration.Save(new Uri(api.Endpoint));
                await rig.Vault.SaveAsync(rig.Key, Token);
            }
            return rig;
        }

        public UploadCoordinator Reopen() => new(new RecordingRepository(Repository.RootDirectory),
            new ApiConfigurationStore(Path.Combine(directory, "settings.json")), Vault, transport, Clock, diagnostic);
        public Task<UploadResult> Send(IProgress<UploadProgress>? progress = null) => Coordinator.ConfirmAndSendAsync(
            Coordinator.Prepare(Id), progress: progress, cancellationToken: Token);
        public Task<JsonDocument> Evidence() => api.CommandAsync(new { operation = "evidence", reference = Id.ToString("D") });

        public void Update(Func<RecordingMetadata, RecordingMetadata> change)
        {
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
            File.WriteAllText(Path.Combine(Repository.DirectoryFor(Id), "metadata.json"), JsonSerializer.Serialize(change(Metadata), options));
        }

        public void AssertIntactAndSecretFree()
        {
            Assert.True(Audio.AsSpan().SequenceEqual(File.ReadAllBytes(Repository.AudioPath(Id))), "Local audio must remain intact.");
            var secrets = Storage.StorageUrls.Append(Key).Append("X-Amz-Signature").ToArray();
            var artifacts = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("audio.wav", StringComparison.Ordinal)).Select(File.ReadAllText).Append(log.ToString());
            foreach (var artifact in artifacts)
                foreach (var secret in secrets)
                    Assert.False(artifact.Contains(secret, StringComparison.Ordinal), "Secret must be absent from files and diagnostics.");
        }

        public async ValueTask DisposeAsync()
        {
            AssertIntactAndSecretFree();
            transport.Dispose();
            await recording.DisposeAsync();
            log.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
