using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Whisper.Recorder.Core.Tests.Fixtures;

/// <summary>Real HTTP to the repository API. Secrets travel only in private pipes and request headers.</summary>
public sealed class V06ApiFixture : IAsyncLifetime
{
    private Process process = null!;
    private Task<string> errors = null!;
    public string Endpoint { get; private set; } = "";
    private Dictionary<string, string> keys = [];
    public string Key(string name) => keys[name];

    public async ValueTask InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "pyproject.toml"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found for V06 API fixture.");
        var python = Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")
            ?? Path.Combine(root.FullName, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = root.FullName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(Path.Combine(root.FullName, "clients/windows-recorder/tests/Core.Tests/Fixtures/v06_api.py"));
        process = Process.Start(info) ?? throw new InvalidOperationException("Could not start V06 API fixture.");
        errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var ready = await ReadAsync();
            Endpoint = ready.RootElement.GetProperty("endpoint").GetString()!;
            keys = ready.RootElement.GetProperty("keys").Deserialize<Dictionary<string, string>>()!;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            // Avoid including startup output: future server failures could contain secrets.
            throw new InvalidOperationException("V06 API fixture failed to start. Check the Python development environment.");
        }
    }

    public async Task<JsonDocument> CommandAsync(object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
        return await ReadAsync();
    }

    private async Task<JsonDocument> ReadAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
        if (line is null) throw new InvalidOperationException("V06 API fixture ended unexpectedly.");
        return JsonDocument.Parse(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (process is null) return;
        if (!process.HasExited)
        {
            await process.StandardInput.WriteLineAsync("{\"operation\":\"stop\"}");
            await process.StandardInput.FlushAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        var serverErrors = await errors;
        var success = process.ExitCode == 0 && string.IsNullOrWhiteSpace(serverErrors);
        process.Dispose();
        Assert.True(success, "V06 API fixture must exit cleanly without diagnostic output.");
    }
}
