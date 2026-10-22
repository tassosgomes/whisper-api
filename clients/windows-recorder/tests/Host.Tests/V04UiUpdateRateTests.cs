using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Recording;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// A captura avisa a cada bloco de 10 ms e a cada nível; a interface não pode receber uma atualização por aviso
/// (isso alagou o dispatcher e congelou a janela). Aqui a "interface" é uma thread própria com fila.
/// </summary>
public sealed class V04UiUpdateRateTests : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-rate-" + Guid.NewGuid().ToString("N"));
    private readonly UiThread ui = new();
    private readonly SyntheticCatalog catalog = new();
    private readonly TestClock clock = new();
    private WasapiAudioCapture capture = null!;
    private RecordingSession session = null!;
    private RecordingViewModel recording = null!;
    private RecordingScreenViewModel screen = null!;

    public async ValueTask DisposeAsync()
    {
        await ui.InvokeAsync(async () =>
        {
            await screen.DisposeAsync();
            recording.Dispose();
            await session.DisposeAsync();
            await capture.DisposeAsync();
        });
        ui.Dispose();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task BlocksAndLevelsEveryTenMillisecondsReachTheUiAtABoundedRateAndTheDurationAdvances()
    {
        await ui.InvokeAsync(() =>
        {
            capture = new WasapiAudioCapture(catalog, clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
            var repository = new RecordingRepository(directory);
            session = new RecordingSession(capture, clock, new IdleScheduler(), repository);
            recording = new RecordingViewModel(session, repository);
            screen = new RecordingScreenViewModel(recording, capture);
        });
        await ui.InvokeAsync(() => screen.RefreshDevicesAsync());
        await ui.InvokeAsync(() => screen.StartAsync());
        Assert.True(await ui.InvokeAsync(() => screen.IsRecording));

        var recordingEvents = 0;
        var screenEvents = 0;
        await ui.InvokeAsync(() =>
        {
            recording.PropertyChanged += (_, _) => recordingEvents++;
            screen.PropertyChanged += (_, _) => screenEvents++;
        });
        var postedBefore = ui.Posted;

        // Dois segundos em tempo real: um bloco de 10 ms por fonte (e um nível por bloco) a cada 10 ms.
        const int seconds = 2;
        for (var i = 0; i < seconds * 100; i++)
        {
            catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.5, 480, i * 480L));
            catalog.Latest(AudioSourceKind.SystemLoopback).Emit(Pcm.Int16Stereo(44100, 330, 0.5, 441, i * 441L));
            clock.UtcNow += TimeSpan.FromMilliseconds(10);
            if (i % 10 == 9)
                session.Tick();
            Thread.Sleep(10);
        }
        await Task.Delay(300, TestContext.Current.CancellationToken); // a última atualização pendente chega
        await ui.InvokeAsync(() => { });

        var posted = ui.Posted - postedBefore;
        Assert.InRange(recordingEvents, 2, 20 * seconds + 10);
        Assert.InRange(posted, 1, 20 * seconds + 10);
        // Cronômetro e níveis são avisados por propriedade (6 por atualização), nunca a tela inteira por tique.
        Assert.InRange(screenEvents, 2, (20 * seconds + 10) * 7);
        Assert.Equal("00:00:02", await ui.InvokeAsync(() => screen.DurationText));
        Assert.True(await ui.InvokeAsync(() => screen.MicrophoneLevel) > 0);
        await ui.InvokeAsync(() => screen.StopAsync());
    }
}
