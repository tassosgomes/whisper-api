using System.Net;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Host.Access;
using Whisper.Recorder.Presentation.Access;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// ViewModel de acesso à API ligado ao cofre real do Windows (alvo de teste) e a uma API simulada em memória.
/// Os arquivos ficam em um diretório temporário; nada toca a pasta nem o alvo de produção do app.
/// </summary>
public sealed class V05ApiAccessScreenTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-v05-" + Guid.NewGuid().ToString("N"));
    private readonly string target = "Whisper.Recorder.Test/Screen/" + Guid.NewGuid().ToString("N");
    private readonly string key = "wsk_" + Guid.NewGuid().ToString("N") + ".segredo" + Guid.NewGuid().ToString("N")[..6];
    private HttpStatusCode? status = HttpStatusCode.NotFound;

    public V05ApiAccessScreenTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { new WindowsCredentialVault(target).RemoveAsync().AsTask().GetAwaiter().GetResult(); }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class StubHandler(Func<HttpStatusCode?> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            status() is { } code
                ? Task.FromResult(new HttpResponseMessage(code))
                : throw new HttpRequestException("sem conexão");
    }


    /// <summary>Uma "sessão do app": instâncias novas sobre o mesmo cofre e o mesmo diretório.</summary>
    private sealed class Session : IDisposable
    {
        private readonly StreamWriter log;
        private readonly ApiAccessProbe probe;
        public ApiAccessViewModel ViewModel { get; }

        public Session(V05ApiAccessScreenTests owner)
        {
            log = new StreamWriter(new FileStream(Path.Combine(owner.directory, "diagnostics.log"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            probe = new ApiAccessProbe(new StubHandler(() => owner.status));
            ViewModel = new ApiAccessViewModel(new ApiAccessService(
                new WindowsCredentialVault(owner.target),
                new ApiConfigurationStore(Path.Combine(owner.directory, "settings.json")),
                probe, new DiagnosticLog(log, new SystemClock())));
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            probe.Dispose();
            log.Dispose();
        }
    }

    /// <summary>Nenhum arquivo do diretório do app (configuração e diagnostics) pode conter as chaves.</summary>
    private void AssertKeysAbsentFromFiles(params string[] keys)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var content = reader.ReadToEnd();
            foreach (var secret in keys)
                Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SavedKeySurvivesRestartIsMaskedAndNeverWrittenToFiles()
    {
        using (var first = new Session(this))
        {
            await first.ViewModel.SaveAsync("https://api.example.test", key, Token);
            Assert.Equal(ApiAccessStatus.Confirmed, first.ViewModel.Status);
            Assert.Equal("Acesso à API confirmado.", first.ViewModel.Message);
            Assert.Equal("••••" + key[^4..], first.ViewModel.MaskedKey);
        }
        Assert.Equal(key, await new WindowsCredentialVault(target).ReadAsync(Token));

        using (var restarted = new Session(this))
        {
            await restarted.ViewModel.InitializeAsync(Token);
            Assert.Equal(ApiAccessStatus.Confirmed, restarted.ViewModel.Status);
            Assert.Equal("••••" + key[^4..], restarted.ViewModel.MaskedKey);
            Assert.Equal("https://api.example.test/v1", restarted.ViewModel.BaseAddress!.AbsoluteUri);
        }

        AssertKeysAbsentFromFiles(key, key[^8..]);
        Assert.Contains("apiBaseAddress", File.ReadAllText(Path.Combine(directory, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovalErasesTheVaultAndFreshStartAsksOnlyWhenSending()
    {
        using var session = new Session(this);
        var requests = 0;
        session.ViewModel.ConfigurationRequested += () => requests++;
        await session.ViewModel.SaveAsync("https://api.example.test", key, Token);
        Assert.True(session.ViewModel.CanSend);

        await session.ViewModel.RemoveAsync(Token);
        Assert.Null(await new WindowsCredentialVault(target).ReadAsync(Token));
        Assert.Equal(ApiAccessStatus.Absent, session.ViewModel.Status);
        Assert.Null(session.ViewModel.MaskedKey);
        Assert.True(session.ViewModel.CanRecord);
        Assert.Equal(0, requests); // gravar e abrir o app nunca pedem configuração

        Assert.False(await session.ViewModel.RequestSendAsync(Token));
        Assert.Equal(1, requests); // o envio é que pede
        AssertKeysAbsentFromFiles(key);
    }

    [Fact]
    public async Task RejectedKeyAndUnreachableApiShowTheRightMessagesAndKeepThePreviousKey()
    {
        using var session = new Session(this);
        await session.ViewModel.SaveAsync("https://api.example.test", key, Token);
        var wrong = "wsk_errada_" + Guid.NewGuid().ToString("N");

        status = HttpStatusCode.Unauthorized;
        await session.ViewModel.SaveAsync("https://api.example.test", wrong, Token);
        Assert.Equal(ApiAccessStatus.Rejected, session.ViewModel.Status);
        Assert.Contains("não foi aceita", session.ViewModel.Message, StringComparison.Ordinal);

        status = null;
        await session.ViewModel.SaveAsync("https://api.example.test", wrong, Token);
        Assert.Equal(ApiAccessStatus.Unverifiable, session.ViewModel.Status);
        Assert.Contains("Não foi possível verificar", session.ViewModel.Message, StringComparison.Ordinal);

        Assert.Equal(key, await new WindowsCredentialVault(target).ReadAsync(Token));
        AssertKeysAbsentFromFiles(key, wrong);
    }

    [Fact]
    public async Task ReplacingTheKeyOverwritesTheVaultAndLeavesNoTraceOfEither()
    {
        using var session = new Session(this);
        var replacement = "wsk_nova_" + Guid.NewGuid().ToString("N");
        await session.ViewModel.SaveAsync("https://api.example.test", key, Token);
        await session.ViewModel.SaveAsync("https://api.example.test", replacement, Token);

        Assert.Equal(replacement, await new WindowsCredentialVault(target).ReadAsync(Token));
        Assert.Equal("••••" + replacement[^4..], session.ViewModel.MaskedKey);
        AssertKeysAbsentFromFiles(key, replacement);
    }
}
