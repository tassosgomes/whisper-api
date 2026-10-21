using Whisper.Recorder.Core.Access;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class AtomicReplaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-atomic-" + Guid.NewGuid());

    [Fact]
    public async Task SaveWaitsForAnotherProcessToReleaseTheDestination()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new ApiConfigurationStore(path);
        store.Save(new Uri("https://old.example/"));

        // Como um antivírus: segura o destino aberto sem compartilhar exclusão; no Windows a troca é recusada.
        Task saving;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saving = Task.Run(() => store.Save(new Uri("https://new.example/")), TestContext.Current.CancellationToken);
            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
        await saving;

        Assert.Equal("new.example", store.Read()!.Host);
        Assert.Single(Directory.GetFiles(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
