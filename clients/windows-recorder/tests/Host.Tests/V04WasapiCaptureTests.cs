using System.Runtime.InteropServices;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>Captura WASAPI do host com fontes sintéticas, integrada à sessão real do núcleo. Sem hardware.</summary>
public sealed class V04WasapiCaptureTests : IAsyncDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticCatalog catalog = new();
    private readonly TestClock clock = new();
    private readonly WasapiAudioCapture capture;
    private readonly RecordingRepository repository;
    private readonly RecordingSession session;
    private readonly DateTimeOffset origin;

    public V04WasapiCaptureTests()
    {
        origin = clock.UtcNow;
        capture = new WasapiAudioCapture(catalog, clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
        repository = new RecordingRepository(directory);
        session = new RecordingSession(capture, clock, new IdleScheduler(), repository);
    }

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();
        await capture.DisposeAsync();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private string WavPath => repository.AudioPath(session.Current!.Id);

    /// <summary>Entrega 100 ms por passo, como o WASAPI: o bloco chega quando o período termina.</summary>
    private void Play(int seconds, bool microphone = true, bool loopback = true, long startStep = 0)
    {
        for (var step = 0; step < seconds * 10; step++)
        {
            clock.UtcNow = origin + TimeSpan.FromMilliseconds((startStep + step + 1) * 100);
            var index = startStep + step;
            if (microphone)
                catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.8, 4800, index * 4800));
            if (loopback)
                catalog.Latest(AudioSourceKind.SystemLoopback).Emit(Pcm.Int16Stereo(44100, 1000, 0.8, 4410, index * 4410));
            session.Tick();
        }
    }

    [Fact]
    public async Task BothSyntheticSourcesAreMixedByTheCoreAndKeepBothVoices()
    {
        await session.StartAsync([SyntheticCatalog.Microphone, SyntheticCatalog.Speakers], Token);
        Play(2);
        clock.UtcNow = origin + TimeSpan.FromSeconds(2);
        await session.StopAsync(Token);

        var samples = Pcm.ReadWav(WavPath);
        Assert.Equal(32000, samples.Length);
        // Cada fonte entra com -6 dB: 0,8 * 0,5 * 32768 = 13107 de pico por voz.
        Assert.InRange(Pcm.Tone(samples, 4000, 8000, 440), 11500, 14500);
        Assert.InRange(Pcm.Tone(samples, 4000, 8000, 1000), 11500, 14500);
        Assert.Equal(RecordingState.Saved, session.Current!.State);
    }

    [Fact]
    public async Task LoopbackThatStopsDeliveringIsFilledWithSilenceByTheCoreNotByTheHost()
    {
        await session.StartAsync([SyntheticCatalog.Microphone, SyntheticCatalog.Speakers], Token);
        Play(1);
        var blocksAfterGap = 0;
        capture.BlockAvailable += block =>
        {
            if (block.Source == AudioSourceKind.SystemLoopback)
                blocksAfterGap++;
        };
        Play(3, loopback: false, startStep: 10);
        clock.UtcNow = origin + TimeSpan.FromSeconds(4);
        await session.StopAsync(Token);

        Assert.Equal(0, blocksAfterGap); // o host não fabrica blocos de silêncio
        var samples = Pcm.ReadWav(WavPath);
        Assert.Equal(64000, samples.Length); // duração igual ao relógio
        Assert.True(Pcm.Tone(samples, 20000, 8000, 440) > 5000);
        Assert.True(Pcm.Tone(samples, 20000, 8000, 1000) < 300);
    }

    [Fact]
    public async Task BlocksCarryTheTimestampOfTheirFirstFrameAndStayContinuous()
    {
        var blocks = new List<AudioPcmBlock>();
        capture.BlockAvailable += blocks.Add;
        await capture.StartAsync(SyntheticCatalog.Microphone, Token);
        var source = catalog.Latest(AudioSourceKind.Microphone);
        for (var step = 1; step <= 5; step++)
        {
            clock.UtcNow = origin + TimeSpan.FromMilliseconds(step * 100 + (step % 2 == 0 ? 15 : -10)); // jitter de entrega
            source.Emit(Pcm.Float32Stereo(48000, 440, 0.5, 4800, 0));
        }

        Assert.Equal(5, blocks.Count);
        Assert.Equal(origin, blocks[0].CapturedAt - TimeSpan.FromMilliseconds(-10));
        for (var i = 1; i < blocks.Count; i++)
            Assert.Equal(TimeSpan.FromMilliseconds(100), blocks[i].CapturedAt - blocks[i - 1].CapturedAt);
        Assert.All(blocks, b => Assert.Equal((48000, 2, 9600), (b.SampleRate, b.Channels, b.Samples.Length)));
    }

    [Fact]
    public void TimestamperReanchorsAfterALongGapAndConvertsFormatsToInt16()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stamper = new BlockTimestamper(16000);
        Assert.Equal(start, stamper.Stamp(start + TimeSpan.FromSeconds(1), 16000));
        Assert.Equal(start + TimeSpan.FromSeconds(1), stamper.Stamp(start + TimeSpan.FromSeconds(2), 16000));
        // 30 s sem dados (loopback em silêncio): o próximo bloco volta ao relógio de parede.
        Assert.Equal(start + TimeSpan.FromSeconds(31), stamper.Stamp(start + TimeSpan.FromSeconds(32), 16000));

        var floats = new byte[8];
        BitConverter.TryWriteBytes(floats.AsSpan(0), 1.5f);
        BitConverter.TryWriteBytes(floats.AsSpan(4), -0.5f);
        var converted = PcmConverter.ToInt16(floats, new CaptureFormat(48000, 1, 32, true), out var peak);
        Assert.Equal([short.MaxValue, (short)-16384], converted);
        Assert.InRange(peak, 0.999f, 1f);
        var int24 = PcmConverter.ToInt16([0x00, 0x00, 0x40], new CaptureFormat(48000, 1, 24, false), out _);
        Assert.Equal((short)0x4000, Assert.Single(int24));
    }

    [Fact]
    public async Task LostDeviceRaisesEventKeepsRecordingWithTheOtherSourceAndReturnsWhenItIsBack()
    {
        var events = new List<AudioDeviceAvailability>();
        capture.DeviceAvailabilityChanged += events.Add;
        await session.StartAsync([SyntheticCatalog.Microphone, SyntheticCatalog.Speakers], Token);
        Play(1);

        catalog.Unplug(SyntheticCatalog.Microphone);

        Assert.Equal((SyntheticCatalog.Microphone.Id, false), (events[0].Device.Id, events[0].IsAvailable));
        Assert.True(session.IsRecording);
        Assert.Contains(RecordingWarning.DeviceLost, session.Current!.Warnings);
        Assert.False(session.SourceAvailability[AudioSourceKind.Microphone]);
        Play(1, microphone: false, startStep: 10);

        var opened = catalog.OpenCount;
        catalog.Replug(SyntheticCatalog.Microphone);
        await WaitUntil(() => events.Count == 2);
        Assert.Equal((SyntheticCatalog.Microphone.Id, true), (events[1].Device.Id, events[1].IsAvailable));
        Assert.Equal(opened + 1, catalog.OpenCount);
        Assert.True(session.SourceAvailability[AudioSourceKind.Microphone]);

        Play(1, startStep: 20);
        clock.UtcNow = origin + TimeSpan.FromSeconds(3);
        await session.StopAsync(Token);
        var samples = Pcm.ReadWav(WavPath);
        Assert.Equal(48000, samples.Length);
        Assert.True(Pcm.Tone(samples, 24000, 8000, 440) < 300);   // sem o microfone
        Assert.True(Pcm.Tone(samples, 24000, 8000, 1000) > 5000); // o sistema seguiu gravando
        Assert.True(Pcm.Tone(samples, 40000, 4000, 440) > 5000);  // microfone de volta
    }

    [Fact]
    public async Task DeviceThatFailsInsideTheStreamIsTreatedAsLost()
    {
        var events = new List<AudioDeviceAvailability>();
        capture.DeviceAvailabilityChanged += events.Add;
        await capture.StartAsync(SyntheticCatalog.Speakers, Token);

        catalog.Latest(AudioSourceKind.SystemLoopback).Fail(new InvalidOperationException("dispositivo invalidado"));

        var lost = Assert.Single(events);
        Assert.False(lost.IsAvailable);
        Assert.Equal(SyntheticCatalog.Speakers, lost.Device);
    }

    [Fact]
    public async Task StartingAnAlreadyOpenSourceKeepsItAndAMeterWorksBeforeRecording()
    {
        var levels = new List<AudioLevel>();
        capture.LevelChanged += levels.Add;
        await capture.StartAsync(SyntheticCatalog.Microphone, Token);       // pré-visualização
        catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.5, 4800, 0));
        Assert.InRange(levels.Last().Peak, 0.45f, 0.5f);

        await capture.StartAsync(SyntheticCatalog.Microphone, Token);       // a gravação começa
        Assert.Equal(1, catalog.OpenCount);

        clock.UtcNow += TimeSpan.FromSeconds(1);
        capture.PumpIdleLevels();
        Assert.Equal(0f, levels.Last().Peak);                                // sem dados, o nível zera
    }

    [Fact]
    public async Task WindowsDenyingTheMicrophoneSurfacesAsUnauthorizedAccess()
    {
        catalog.OpenFailure = new COMException("negado", unchecked((int)0x80070005));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await capture.StartAsync(SyntheticCatalog.Microphone, Token));
        catalog.OpenFailure = null;
        catalog.DeniedIds.Add(SyntheticCatalog.Microphone.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await capture.StartAsync(SyntheticCatalog.Microphone, Token));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, Token);
        Assert.True(condition());
    }
}
