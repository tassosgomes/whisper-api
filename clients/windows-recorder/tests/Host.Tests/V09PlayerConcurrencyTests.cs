using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Playback;
using Whisper.Recorder.Host.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Player e threads (V-09, host): a saída real lê o arquivo em outra thread, então a busca da interface e o descarte
/// precisam conviver com a leitura. A saída de mentira lê em outra thread de verdade; nada toca nos alto-falantes.
/// </summary>
public sealed class V09PlayerConcurrencyTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string root = Path.Combine(Path.GetTempPath(), "whisper-host-v09-threads-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingRepository repository;

    public V09PlayerConcurrencyTests() => repository = new RecordingRepository(Path.Combine(root, "recordings"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private Guid Seed(int seconds)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(repository.DirectoryFor(id));
        repository.Save(new RecordingMetadata(id, "Reunião", DateTimeOffset.UtcNow, RecordingState.Saved,
            TimeSpan.FromSeconds(seconds), [AudioSourceKind.Microphone], []));
        SyntheticWav.Write(repository.AudioPath(id), TimeSpan.FromSeconds(seconds));
        return id;
    }

    [Fact]
    public async Task SeekingWhileTheOutputThreadReadsNeverCorruptsThePositionOrTheReading()
    {
        var id = Seed(60);
        var output = new ThreadedWavePlayer();
        await using var player = new NAudioAudioPlayer(() => output);
        await player.OpenAsync(repository.AudioPath(id), Token);
        await player.PlayAsync(Token);

        // A interface busca sem parar enquanto a thread da saída lê o mesmo arquivo.
        for (var i = 0; i < 3000; i++)
        {
            await player.SeekAsync(TimeSpan.FromSeconds(i % 7 * 5), Token);
            Assert.InRange(player.Position.TotalSeconds, 0, 60);
        }
        await TrackingRig.Eventually(() => output.Reads > 0);

        Assert.Null(output.Failure);
        await player.PauseAsync(Token);
        await player.StopAsync(Token);
    }

    [Fact]
    public async Task DisposingFromAnotherThreadWhileTheOutputReadsReleasesTheFile()
    {
        var id = Seed(60);
        var output = new ThreadedWavePlayer();
        var player = new NAudioAudioPlayer(() => output);
        await player.OpenAsync(repository.AudioPath(id), Token);
        await player.PlayAsync(Token);
        await TrackingRig.Eventually(() => output.Reads > 0);

        // Como no ReleaseAsync do ViewModel: o descarte roda fora da thread que criou o player.
        await Task.Run(async () => await player.DisposeAsync(), Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Null(output.Failure);
        Directory.Delete(repository.DirectoryFor(id), recursive: true);
    }
}
