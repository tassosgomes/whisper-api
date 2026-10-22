using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Playback;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Playback;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Player (V-09, host): o <see cref="NAudioAudioPlayer"/> lê um WAV sintético de verdade e usa uma saída de mentira, então
/// não há dispositivo, alto-falante nem janela. Cobre tocar/pausar, posição, avançar e voltar, o fim do áudio, o erro em
/// texto fixo e a liberação do arquivo.
/// </summary>
public sealed class V09PlayerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string root = Path.Combine(Path.GetTempPath(), "whisper-host-v09-player-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingRepository repository;
    private readonly FakeOutputs outputs = new();

    public V09PlayerTests() => repository = new RecordingRepository(Path.Combine(root, "recordings"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private Guid Seed(int seconds, string title = "Reunião")
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(repository.DirectoryFor(id));
        repository.Save(new RecordingMetadata(id, title, DateTimeOffset.UtcNow, RecordingState.Saved,
            TimeSpan.FromSeconds(seconds), [AudioSourceKind.Microphone], []));
        SyntheticWav.Write(repository.AudioPath(id), TimeSpan.FromSeconds(seconds));
        return id;
    }

    private PlayerScreenViewModel NewScreen() => new(outputs.PlayerFactory, repository);

    [Fact]
    public async Task PlaysPausesAndMovesForwardAndBackInTenSecondSteps()
    {
        var screen = NewScreen();
        await screen.OpenAsync(Seed(60, "Reunião semanal"));
        Assert.Equal("Reunião semanal", screen.Title);
        Assert.Equal("00:01:00", screen.DurationText);
        Assert.Equal("Parado", screen.StateText);
        Assert.Equal("Reproduzir", screen.PlayPauseText);

        await screen.PlayPause.ExecuteAsync();
        Assert.True(screen.IsPlaying);
        Assert.Equal("Reproduzindo", screen.StateText);
        Assert.Equal("Pausar", screen.PlayPauseText);

        outputs.Latest.Pump(TimeSpan.FromSeconds(3.2));
        screen.Tick();
        Assert.Equal("00:00:03", screen.PositionText);

        await screen.Forward.ExecuteAsync();
        Assert.Equal(13.2, screen.Position.TotalSeconds, 2);
        await screen.Back.ExecuteAsync();
        Assert.Equal(3.2, screen.Position.TotalSeconds, 2);
        // Voltar além do início fica no início; avançar além do fim fica no fim.
        await screen.Back.ExecuteAsync();
        Assert.Equal(TimeSpan.Zero, screen.Position);
        await screen.SeekToAsync(TimeSpan.FromSeconds(55));
        await screen.Forward.ExecuteAsync();
        Assert.Equal(60, screen.Position.TotalSeconds, 2);

        await screen.SeekToAsync(TimeSpan.FromSeconds(20));
        await screen.PlayPause.ExecuteAsync();
        Assert.False(screen.IsPlaying);
        Assert.Equal("Pausado", screen.StateText);
        Assert.Equal("Reproduzir", screen.PlayPauseText);
        Assert.Equal(NAudio.Wave.PlaybackState.Paused, outputs.Latest.PlaybackState);

        await screen.ReleaseAsync();
    }

    [Fact]
    public async Task TheSliderValueSeeksAndTheEndOfTheAudioIsShownInText()
    {
        var screen = NewScreen();
        await screen.OpenAsync(Seed(10));
        Assert.Equal(10, screen.MaximumSeconds);

        screen.PositionSeconds = 4;
        Assert.Equal(4, screen.Position.TotalSeconds, 2);

        await screen.PlayPause.ExecuteAsync();
        outputs.Latest.Pump(TimeSpan.FromSeconds(30));
        screen.Tick();
        Assert.False(screen.IsPlaying);
        Assert.Equal("Fim da gravação", screen.StateText);
        Assert.Equal("00:00:10", screen.PositionText);

        // Reproduzir de novo no fim recomeça do início, com uma saída nova.
        await screen.PlayPause.ExecuteAsync();
        Assert.True(screen.IsPlaying);
        Assert.Equal(TimeSpan.Zero, screen.Position);
        Assert.Equal(2, outputs.Created.Count);
        Assert.True(outputs.Created[0].Disposed);
        await screen.ReleaseAsync();
    }

    [Fact]
    public async Task AnUnreadableFileShowsAFixedMessageWithoutTheLocalPath()
    {
        var id = Seed(5);
        await File.WriteAllTextAsync(repository.AudioPath(id), "isto não é um WAV", Token);
        var screen = NewScreen();
        await screen.OpenAsync(id);

        Assert.False(screen.IsOpen);
        Assert.True(screen.HasError);
        Assert.Contains("Não foi possível abrir o áudio", screen.StateText);
        Assert.DoesNotContain(root, screen.StateText, StringComparison.OrdinalIgnoreCase);
        Assert.False(screen.PlayPause.CanExecute(null));
        // O arquivo ilegível não fica preso: dá para apagá-lo.
        File.Delete(repository.AudioPath(id));
    }

    [Fact]
    public async Task ReleasingClosesTheAudioFileSoTheRecordingCanBeDeleted()
    {
        var id = Seed(30);
        var screen = NewScreen();
        await screen.OpenAsync(id);
        await screen.PlayPause.ExecuteAsync();

        // Aberto no player, o WAV não pode ser apagado (é o que impediria "apagar").
        Assert.Throws<IOException>(() => Directory.Delete(repository.DirectoryFor(id), recursive: true));
        Assert.Equal(id, screen.RecordingId);

        await screen.ReleaseAsync();

        Assert.Null(screen.RecordingId);
        Assert.False(screen.IsOpen);
        Assert.True(outputs.Latest.Disposed);
        Directory.Delete(repository.DirectoryFor(id), recursive: true);
        Assert.False(Directory.Exists(repository.DirectoryFor(id)));
        // Liberar de novo é inofensivo.
        await screen.ReleaseAsync();
    }

    [Fact]
    public async Task OpeningAnotherRecordingReleasesThePreviousFile()
    {
        var first = Seed(20);
        var second = Seed(20);
        var screen = NewScreen();
        await screen.OpenAsync(first);
        await screen.OpenAsync(second);

        Directory.Delete(repository.DirectoryFor(first), recursive: true);
        Assert.Throws<IOException>(() => Directory.Delete(repository.DirectoryFor(second), recursive: true));
        await screen.ReleaseAsync();
    }

    [Fact]
    public async Task TheAdapterStopsAtTheStartAndReportsDurationAndPosition()
    {
        var id = Seed(12);
        await using var player = new NAudioAudioPlayer(outputs.Create);
        await player.OpenAsync(repository.AudioPath(id), Token);
        Assert.Equal(12, player.Duration.TotalSeconds, 2);
        Assert.Equal(TimeSpan.Zero, player.Position);

        await player.SeekAsync(TimeSpan.FromSeconds(7), Token);
        Assert.Equal(7, player.Position.TotalSeconds, 2);
        await player.SeekAsync(TimeSpan.FromSeconds(99), Token);
        Assert.Equal(12, player.Position.TotalSeconds, 2);

        await player.PlayAsync(Token);
        Assert.True(player.IsPlaying);
        Assert.Equal(TimeSpan.Zero, player.Position); // no fim, tocar recomeça do início
        await player.PauseAsync(Token);
        Assert.False(player.IsPlaying);
        await player.StopAsync(Token);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }
}
