using Whisper.Recorder.Host.Access;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Cofre sobre o Credential Manager real do usuário. Cada instância da classe usa um alvo próprio de teste
/// (nunca o de produção) e o remove no <see cref="Dispose"/>, que o xUnit executa também quando o teste falha.
/// </summary>
public sealed class V05CredentialVaultTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string target = "Whisper.Recorder.Test/ApiKey/" + Guid.NewGuid().ToString("N");

    public void Dispose() => new WindowsCredentialVault(target).RemoveAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task SavesReadsOverwritesAndRemovesTheKey()
    {
        var vault = new WindowsCredentialVault(target);
        Assert.Null(await vault.ReadAsync(Token));

        await vault.SaveAsync("wsk_first.secret-ção", Token);
        Assert.Equal("wsk_first.secret-ção", await vault.ReadAsync(Token));

        await vault.SaveAsync("wsk_second.secret", Token);
        Assert.Equal("wsk_second.secret", await vault.ReadAsync(Token));

        await vault.RemoveAsync(Token);
        Assert.Null(await vault.ReadAsync(Token));
        await vault.RemoveAsync(Token); // remover o que não existe não falha
    }

    [Fact]
    public async Task NewInstanceReadsWhatThePreviousOneSaved()
    {
        var key = "wsk_" + Guid.NewGuid().ToString("N");
        await new WindowsCredentialVault(target).SaveAsync(key, Token);

        // Outra instância, como no reinício do app: nada vem da memória do objeto anterior.
        Assert.Equal(key, await new WindowsCredentialVault(target).ReadAsync(Token));

        await new WindowsCredentialVault(target).RemoveAsync(Token);
        Assert.Null(await new WindowsCredentialVault(target).ReadAsync(Token));
    }

    [Fact]
    public async Task ProductionTargetIsDistinctAndNeverTouchedByTests()
    {
        Assert.NotEqual(WindowsCredentialVault.ProductionTarget, target);
        Assert.StartsWith("Whisper.Recorder.Test/", target, StringComparison.Ordinal);
        // O valor do alvo do app nunca é lido aqui: o teste não pode tocar na chave real do usuário.
        await new WindowsCredentialVault(target).SaveAsync("wsk_isolated", Token);
        Assert.Equal("wsk_isolated", await new WindowsCredentialVault(target).ReadAsync(Token));
    }

    [Fact]
    public async Task InvalidInputIsRefusedWithoutEchoingTheKey()
    {
        var vault = new WindowsCredentialVault(target);
        await Assert.ThrowsAsync<ArgumentException>(async () => await vault.SaveAsync("", Token));
        var oversized = new string('k', 3000);
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await vault.SaveAsync(oversized, Token));
        Assert.DoesNotContain(oversized, error.Message, StringComparison.Ordinal);
        Assert.Null(await vault.ReadAsync(Token));
    }
}
