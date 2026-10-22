using System.Net;
using System.Text.Json;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tests.Doubles;
using Whisper.Recorder.Core.Tests.Fixtures;
using Whisper.Recorder.Presentation.Access;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V05ApiAccessTests(V05ApiFixture api) : IClassFixture<V05ApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshInstallationRecordsLocallyAndOnlySendRequestsConfiguration()
    {
        using var rig = new AccessRig();
        var navigation = 0;
        rig.ViewModel.ConfigurationRequested += () => navigation++;
        await rig.ViewModel.InitializeAsync(Token);
        Assert.Equal(ApiAccessStatus.Absent, rig.ViewModel.Status);
        Assert.True(rig.ViewModel.CanRecord);
        Assert.False(rig.ViewModel.CanSend);
        Assert.Equal(0, navigation);
        Assert.False(await rig.ViewModel.RequestSendAsync(Token));
        Assert.Equal(1, navigation);
        Assert.Null(rig.ViewModel.MaskedKey);

        var clock = new FakeClock();
        await using var capture = new FakeAudioCapture();
        var repository = new RecordingRepository(Path.Combine(rig.Directory, "recordings"));
        await using var recording = new RecordingSession(capture, clock, new SystemScheduler(), repository);
        await recording.StartAsync([new AudioDevice("microphone", "Test microphone", AudioSourceKind.Microphone, true)], Token);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        await recording.StopAsync(Token);
        Assert.Equal(RecordingState.Saved, Assert.Single(repository.List()).State);
    }

    [Fact]
    public async Task RealApiAcceptsKeyOnMissingJobAndOnlyVaultReceivesSecret()
    {
        using var rig = new AccessRig();
        var notifications = 0;
        rig.ViewModel.PropertyChanged += (_, _) => notifications++;
        var key = api.Key("valid");
        await rig.ViewModel.SaveAsync(api.Endpoint, key, Token);
        using var evidence = await api.CommandAsync(new { operation = "evidence" });
        Assert.True(rig.ViewModel.Status == ApiAccessStatus.Confirmed,
            $"Access: {rig.ViewModel.Status}; problem: {rig.Service.State.Problem}; HTTP: {evidence.RootElement.GetRawText()}");
        Assert.True(rig.ViewModel.CanSend);
        Assert.Equal(api.Endpoint + "/v1", rig.ViewModel.BaseAddress!.AbsoluteUri);
        Assert.Equal("••••" + key[^4..], rig.ViewModel.MaskedKey);
        Assert.True(key == await rig.Vault.ReadAsync(Token), "Only the vault must contain the accepted key.");
        Assert.True(notifications > 0);
        AssertSecretAbsent(rig, key);
        using var config = JsonDocument.Parse(File.ReadAllText(rig.ConfigPath));
        Assert.Equal("apiBaseAddress", Assert.Single(config.RootElement.EnumerateObject()).Name);

        Assert.Equal(0, evidence.RootElement.GetProperty("jobs").GetInt32());
        Assert.Contains(evidence.RootElement.GetProperty("requests").EnumerateArray(), request =>
            request.GetProperty("method").GetString() == "GET"
            && request.GetProperty("path").GetString()!.StartsWith("/v1/transcriptions/trn_", StringComparison.Ordinal)
            && request.GetProperty("status").GetInt32() == 404);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("revoked")]
    [InlineData("inactive_account")]
    public async Task RealApiRejectsInvalidRevokedAndAccountWithoutActiveCredentials(string name)
    {
        using var rig = new AccessRig();
        var key = name == "invalid" ? "wsk_00000000000000000000000000000000.invalid" : api.Key(name);
        await rig.ViewModel.SaveAsync(api.Endpoint + "/v1/", key, Token);
        Assert.Equal(ApiAccessStatus.Rejected, rig.ViewModel.Status);
        Assert.Contains("não foi aceita", rig.ViewModel.Message);
        Assert.False(rig.ViewModel.CanSend);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.False(File.Exists(rig.ConfigPath));
        AssertSecretAbsent(rig, key);
    }

    [Fact]
    public async Task ReopeningVerifiesSavedKeyAndShowsOnlySuffix()
    {
        using var rig = new AccessRig();
        var key = api.Key("valid");
        await rig.ViewModel.SaveAsync(api.Endpoint, key, Token);
        var reopened = new ApiAccessService(rig.Vault, new ApiConfigurationStore(rig.ConfigPath), rig.Probe, rig.Diagnostic);
        using var viewModel = new ApiAccessViewModel(reopened);
        await viewModel.InitializeAsync(Token);
        Assert.Equal(ApiAccessStatus.Confirmed, viewModel.Status);
        Assert.Equal("••••" + key[^4..], viewModel.MaskedKey);
        Assert.True(await viewModel.RequestSendAsync(Token));
        AssertSecretAbsent(rig, key);
    }

    [Fact]
    public async Task ReplacementAndRemovalUpdateSingleVaultEntryAndSendAccess()
    {
        using var rig = new AccessRig();
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        await rig.ViewModel.SaveAsync(api.Endpoint + "/v1/", api.Key("replacement"), Token);
        Assert.True(api.Key("replacement") == await rig.Vault.ReadAsync(Token), "Vault must contain the replacement key.");
        Assert.Equal("••••" + api.Key("replacement")[^4..], rig.ViewModel.MaskedKey);
        Assert.Equal(ApiAccessStatus.Confirmed, rig.ViewModel.Status);
        await rig.ViewModel.RemoveAsync(Token);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.Null(rig.ViewModel.MaskedKey);
        Assert.Equal(ApiAccessStatus.Absent, rig.ViewModel.Status);
        Assert.False(await rig.ViewModel.RequestSendAsync(Token));
        Assert.True(rig.ViewModel.CanRecord);
        AssertSecretAbsent(rig, api.Key("valid"), api.Key("replacement"));
    }

    [Fact]
    public async Task InvalidReplacementPreservesSavedAddressAndKey()
    {
        using var rig = new AccessRig();
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        var savedConfig = File.ReadAllText(rig.ConfigPath);
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("revoked"), Token);
        Assert.Equal(ApiAccessStatus.Rejected, rig.ViewModel.Status);
        Assert.True(api.Key("valid") == await rig.Vault.ReadAsync(Token), "Vault must preserve the accepted key.");
        Assert.Equal(savedConfig, File.ReadAllText(rig.ConfigPath));
        Assert.True(await rig.ViewModel.RequestSendAsync(Token));
        AssertSecretAbsent(rig, api.Key("valid"), api.Key("revoked"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(403)]
    [InlineData(200)]
    [InlineData(302)]
    public async Task ServerAndUnexpectedStatusesAreUnverifiableAndNeverPersisted(int code)
    {
        const string key = "candidate-secret-for-response-test";
        using var rig = new AccessRig(new DelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(key == Assert.Single(request.Headers.GetValues("X-API-Key")), "Key must be sent only in the authentication header.");
            Assert.False(request.RequestUri!.AbsoluteUri.Contains(key, StringComparison.Ordinal), "Key must not be part of the request URL.");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)
            {
                Content = new StringContent("server echo " + key)
            });
        }));
        await rig.ViewModel.SaveAsync("https://api.example.test", key, Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Contains("Não foi possível verificar", rig.ViewModel.Message);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.False(File.Exists(rig.ConfigPath));
        AssertSecretAbsent(rig, key);
    }

    [Theory]
    [InlineData(401, ApiAccessStatus.Rejected)]
    [InlineData(404, ApiAccessStatus.Confirmed)]
    public async Task StatusCodeDeterminesAccessEvenWithUnparseableErrorBody(int code, ApiAccessStatus expected)
    {
        const string key = "body-must-not-be-parsed-secret";
        using var rig = new AccessRig(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("not json " + key) })));
        await rig.ViewModel.SaveAsync("https://api.example.test", key, Token);
        Assert.Equal(expected, rig.ViewModel.Status);
        AssertSecretAbsent(rig, key);
    }

    [Fact]
    public async Task NetworkExceptionCannotLeakKeyOrMarkItInvalid()
    {
        const string key = "network-exception-secret";
        using var rig = new AccessRig(new DelegateHandler((_, _) => throw new HttpRequestException("failure " + key)));
        await rig.ViewModel.SaveAsync("https://api.example.test", key, Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.False(File.Exists(rig.ConfigPath));
        AssertSecretAbsent(rig, key);
    }

    [Fact]
    public async Task VerificationTimeoutIsUnverifiableAndAllowsRetry()
    {
        var unavailable = true;
        using var rig = new AccessRig(new DelegateHandler(async (_, token) =>
        {
            if (unavailable) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }), TimeSpan.FromMilliseconds(100));
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        unavailable = false;
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        Assert.Equal(ApiAccessStatus.Confirmed, rig.ViewModel.Status);
    }

    [Fact]
    public async Task CallerCancellationDoesNotTurnIntoRejectionOrSaveKey()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new AccessRig(new DelegateHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var saving = rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), cancellation.Token);
        await entered.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        Assert.Equal(ApiAccessStatus.Absent, rig.ViewModel.Status);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.False(File.Exists(rig.ConfigPath));
    }

    [Fact]
    public async Task UnavailableReplacementPreservesExistingConfigurationAndCanRetry()
    {
        var fail = false;
        using var rig = new AccessRig(new DelegateHandler((_, _) => fail
            ? throw new HttpRequestException("offline")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        var savedConfig = File.ReadAllText(rig.ConfigPath);
        fail = true;
        await rig.ViewModel.SaveAsync("https://other.example.test", api.Key("replacement"), Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Equal(savedConfig, File.ReadAllText(rig.ConfigPath));
        Assert.True(api.Key("valid") == await rig.Vault.ReadAsync(Token), "Vault must preserve the accepted key.");
        Assert.False(await rig.ViewModel.RequestSendAsync(Token));
        fail = false;
        Assert.True(await rig.ViewModel.RequestSendAsync(Token));
    }

    [Fact]
    public async Task RevocationAfterSavingBlocksNextSendAndRequestsSettings()
    {
        // Use a separate fixture so revocation cannot affect the shared valid key.
        await using var isolated = new V05ApiFixture();
        await isolated.InitializeAsync();
        using var rig = new AccessRig();
        await rig.ViewModel.SaveAsync(isolated.Endpoint, isolated.Key("valid"), Token);
        Assert.True(rig.ViewModel.CanSend);
        using var result = await isolated.CommandAsync(new { operation = "revoke", name = "valid" });
        var navigation = 0;
        rig.ViewModel.ConfigurationRequested += () => navigation++;
        Assert.False(await rig.ViewModel.RequestSendAsync(Token));
        Assert.Equal(ApiAccessStatus.Rejected, rig.ViewModel.Status);
        Assert.Equal(1, navigation);
        Assert.True(rig.ViewModel.CanRecord);
        AssertSecretAbsent(rig, isolated.Key("valid"));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("file:///tmp/api")]
    [InlineData("https://user:password@api.example.test")]
    [InlineData("https://api.example.test?apiKey=address-secret")]
    [InlineData("https://api.example.test#address-secret")]
    [InlineData("https://api.example.test/address-secret")]
    public async Task InvalidOrSecretBearingAddressIsNotSentOrPersisted(string address)
    {
        using var rig = new AccessRig(new DelegateHandler((_, _) => throw new InvalidOperationException("Must not send")));
        await rig.ViewModel.SaveAsync(address, "address-secret", Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Equal(ApiAccessProblem.InvalidAddress, rig.Service.State.Problem);
        Assert.Null(await rig.Vault.ReadAsync(Token));
        Assert.False(File.Exists(rig.ConfigPath));
        AssertSecretAbsent(rig, "address-secret");
    }

    [Fact]
    public async Task CommonConfigurationFailureRollsBackVaultAndHidesExceptionDetails()
    {
        using var rig = new AccessRig();
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        File.Delete(rig.ConfigPath);
        System.IO.Directory.CreateDirectory(rig.ConfigPath);
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("replacement"), Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.Equal(ApiAccessProblem.StorageUnavailable, rig.Service.State.Problem);
        Assert.True(api.Key("valid") == await rig.Vault.ReadAsync(Token), "Vault must restore the previous key.");
        Assert.Empty(System.IO.Directory.GetFiles(rig.Directory, "*.tmp"));
        AssertSecretAbsent(rig, api.Key("valid"), api.Key("replacement"));
    }

    [Fact]
    public async Task VaultFailuresAreSafeAndDoNotReportRemovalOrSaveSuccess()
    {
        var vault = new FailingVault();
        using var rig = new AccessRig(vault: vault);
        vault.FailSave = true;
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.False(File.Exists(rig.ConfigPath));
        Assert.Null(await vault.ReadAsync(Token));
        AssertSecretAbsent(rig, api.Key("valid"));
        vault.FailSave = false;
        await rig.ViewModel.SaveAsync(api.Endpoint, api.Key("valid"), Token);
        vault.FailRemove = true;
        await rig.ViewModel.RemoveAsync(Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, rig.ViewModel.Status);
        Assert.True(api.Key("valid") == await vault.ReadAsync(Token), "Failed removal must preserve the key.");
        AssertSecretAbsent(rig, api.Key("valid"));
    }

    [Fact]
    public async Task ShortKeyIsNeverRevealedInFullEvenAfterAcceptance()
    {
        using var rig = new AccessRig(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        await rig.ViewModel.SaveAsync("https://api.example.test", "abc", Token);
        Assert.Equal("••••", rig.ViewModel.MaskedKey);
        AssertSecretAbsent(rig, "abc");
    }

    private static void AssertSecretAbsent(AccessRig rig, params string[] keys)
    {
        var visible = JsonSerializer.Serialize(rig.Service.State) + rig.ViewModel.Message + rig.ViewModel.MaskedKey + rig.Log;
        foreach (var file in System.IO.Directory.GetFiles(rig.Directory, "*", SearchOption.AllDirectories))
            if (Path.GetExtension(file) != ".wav") visible += File.ReadAllText(file);
        foreach (var key in keys)
            Assert.False(visible.Contains(key, StringComparison.Ordinal), "An API key leaked into configuration, presentation state or diagnostics.");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FailingVault : ICredentialVault
    {
        private readonly FakeCredentialVault inner = new();
        public bool FailSave { get; set; }
        public bool FailRemove { get; set; }
        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default) => FailSave
            ? throw new IOException("vault failure " + apiKey) : inner.SaveAsync(apiKey, cancellationToken);
        public ValueTask RemoveAsync(CancellationToken cancellationToken = default) => FailRemove
            ? throw new IOException("vault remove failed") : inner.RemoveAsync(cancellationToken);
    }

    private sealed class AccessRig : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "whisper-v05-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Directory, "settings.json");
        public ICredentialVault Vault { get; }
        public ApiAccessProbe Probe { get; }
        public StringWriter Log { get; } = new();
        public DiagnosticLog Diagnostic { get; }
        public ApiAccessService Service { get; }
        public ApiAccessViewModel ViewModel { get; }

        public AccessRig(HttpMessageHandler? handler = null, TimeSpan? timeout = null, ICredentialVault? vault = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Vault = vault ?? new FakeCredentialVault();
            Probe = new ApiAccessProbe(handler, timeout);
            Diagnostic = new DiagnosticLog(Log, new FakeClock());
            Service = new ApiAccessService(Vault, new ApiConfigurationStore(ConfigPath), Probe, Diagnostic);
            ViewModel = new ApiAccessViewModel(Service);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Probe.Dispose();
            Log.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
