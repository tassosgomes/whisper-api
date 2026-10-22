using System.Diagnostics;
using System.Text.RegularExpressions;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Access;
using Whisper.Recorder.Host.Setup;
using Whisper.Recorder.Host.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Instalador (V-10): remoção dos dados pedida pelo desinstalador e contrato do script do Inno Setup.
/// Pasta temporária, alvo de teste do Credential Manager e mutex de teste; nada toca
/// `%LOCALAPPDATA%\Whisper\Recorder`, o alvo `Whisper.Recorder/ApiKey` nem o mutex do app.
/// </summary>
public sealed partial class V10InstallerTests : IDisposable
{
    static V10InstallerTests() => StaleTestCredentials.SweepOnce();

    private readonly string root = Path.Combine(Path.GetTempPath(), "whisper-host-v10-" + Guid.NewGuid().ToString("N"));
    private readonly string target = "Whisper.Recorder.Test/Installer/" + Guid.NewGuid().ToString("N");
    private readonly string mutexName = "Whisper.Recorder.Test.Installer." + Guid.NewGuid().ToString("N");
    private readonly WindowsCredentialVault vault;

    // Imita %LOCALAPPDATA%\Whisper\Recorder dentro da pasta temporária.
    private string DataDirectory => Path.Combine(root, "Whisper", "Recorder");

    public V10InstallerTests()
    {
        Directory.CreateDirectory(root);
        vault = new WindowsCredentialVault(target);
    }

    public void Dispose()
    {
        try { vault.RemoveAsync().AsTask().GetAwaiter().GetResult(); }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private UninstallDataPurge Purge() => new(DataDirectory, vault, mutexName);

    /// <summary>Cria dados como o app deixa: gravação, transcrição, configurações e diário.</summary>
    private void Seed(RecordingState state)
    {
        var repository = new RecordingRepository(Path.Combine(DataDirectory, "recordings"));
        var id = Guid.NewGuid();
        repository.Create(new RecordingMetadata(id, "Reunião", DateTimeOffset.UtcNow, state, TimeSpan.Zero, [], []));
        File.WriteAllText(Path.Combine(repository.DirectoryFor(id), "transcript.txt"), "texto");
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(DataDirectory, "diagnostics.log"), "linha");
    }

    private static bool TryLinkDirectory(string link, string linkTarget)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{linkTarget}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public async Task PurgeRemovesDataKeyAndDoesNotFollowJunctionsOrBlockOnInterruptedOrCorruptRecordings()
    {
        await vault.SaveAsync("wsk_chave-de-teste", TestContext.Current.CancellationToken);
        // Uma gravação que ficou em "recording" (queda do app) e uma salva.
        Seed(RecordingState.Recording);
        Seed(RecordingState.Saved);
        // Metadados corrompidos não bloqueiam a remoção nem deixam a chave no cofre.
        var corrupt = Path.Combine(DataDirectory, "recordings", Guid.NewGuid().ToString());
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "metadata.json"), "{ corrompido");
        var outside = Path.Combine(root, "fora");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "importante.txt"), "preservar");
        Assert.True(TryLinkDirectory(Path.Combine(DataDirectory, "recordings", "link"), outside),
            "Não foi possível criar a junção de teste.");

        var code = Purge().Run();

        Assert.Equal(UninstallDataPurge.Success, code);
        Assert.False(Directory.Exists(DataDirectory));
        Assert.False(Directory.Exists(Path.Combine(root, "Whisper")));
        Assert.Null(await vault.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("preservar", File.ReadAllText(Path.Combine(outside, "importante.txt")));
    }

    [Fact]
    public async Task PurgeRefusesWhileTheAppIsRunningAndDeletesNothing()
    {
        await vault.SaveAsync("wsk_chave-de-teste", TestContext.Current.CancellationToken);
        Seed(RecordingState.Saved);

        // O mutex pertence à thread que o criou: outra thread o segura, como a instância aberta do app.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(true, mutexName);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            var code = Purge().Run();

            Assert.Equal(UninstallDataPurge.AppRunning, code);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        Assert.True(File.Exists(Path.Combine(DataDirectory, "settings.json")));
        Assert.Single(Directory.GetDirectories(Path.Combine(DataDirectory, "recordings")));
        Assert.Equal("wsk_chave-de-teste", await vault.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PurgeWithoutDataDirectorySucceedsAndDoesNotCreateIt()
    {
        var code = Purge().Run();

        Assert.Equal(UninstallDataPurge.Success, code);
        Assert.False(Directory.Exists(DataDirectory));
        Assert.False(Directory.Exists(Path.Combine(root, "Whisper")));
    }

    private static string InstallerScript() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "installer", "WhisperRecorder.iss"));

    [GeneratedRegex(@"^#define\s+(\w+)\s+""([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex DefineLine();

    [Fact]
    public void InstallerScriptKeepsTheSafetyContract()
    {
        var script = InstallerScript();
        var defines = DefineLine().Matches(script).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        // Por usuário, sem elevação e sem opção de elevar.
        Assert.Matches(@"(?m)^PrivilegesRequired=lowest\s*$", script);
        Assert.DoesNotMatch(@"(?m)^PrivilegesRequiredOverridesAllowed", script);
        // Só Windows 11 x64.
        Assert.Matches(@"(?m)^MinVersion=10\.0\.22000\s*$", script);
        Assert.Matches(@"(?m)^ArchitecturesAllowed=x64compatible\s*$", script);
        // O mesmo mutex e o mesmo argumento do código do app.
        Assert.Equal(UninstallDataPurge.AppMutexName, defines["AppMutexName"]);
        Assert.Equal(UninstallDataPurge.Argument, defines["PurgeArgument"]);
        Assert.Matches(@"(?m)^AppMutex=\{#AppMutexName\}\s*$", script);
        Assert.Contains("{#PurgeArgument}", script);
        Assert.Matches(@"(?m)^CloseApplications=yes\s*$", script);
        // Desinstalação silenciosa mantém os dados: a guarda vem antes de qualquer Exec do desinstalador.
        var code = script[script.IndexOf("[Code]", StringComparison.Ordinal)..];
        var silent = code.IndexOf("if UninstallSilent then", StringComparison.Ordinal);
        Assert.True(silent >= 0, "Falta a guarda UninstallSilent.");
        Assert.True(silent < code.IndexOf("Exec(", StringComparison.Ordinal));
        // O instalador não leva dados do usuário.
        Assert.DoesNotContain("{localappdata}\\Whisper", script);
    }
}
