using System.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

// O limite é de tempo de relógio: roda sozinho, sem disputar CPU com os testes que sobem a API.
[CollectionDefinition(nameof(V04TimelinePerformanceTests), DisableParallelization = true)]
public sealed class V04TimelinePerformanceCollection;

[Collection(nameof(V04TimelinePerformanceTests))]
public sealed class V04TimelinePerformanceTests
{
    [Fact]
    public async Task SixtySecondsFromTwoSourcesAt48kHzStereoMixesQuicklyAndAllocatesLittle()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        var microphone = new short[480 * 2];
        var system = new short[480 * 2];
        for (var i = 0; i < microphone.Length; i++)
        {
            microphone[i] = (short)(8000 * Math.Sin(i * 0.05));
            system[i] = (short)(6000 * Math.Sin(i * 0.03));
        }

        GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 6000; i++) // 6000 blocos de 10 ms por fonte = 60 s
        {
            rig.Capture.EmitBlock(new AudioPcmBlock(AudioSourceKind.Microphone, rig.Clock.UtcNow, 48000, 2, microphone));
            rig.Capture.EmitBlock(new AudioPcmBlock(AudioSourceKind.SystemLoopback, rig.Clock.UtcNow, 48000, 2, system));
            rig.Capture.EmitLevel(new AudioLevel(AudioSourceKind.Microphone, 0.5f));
            rig.Capture.EmitLevel(new AudioLevel(AudioSourceKind.SystemLoopback, 0.4f));
            rig.Clock.UtcNow += TimeSpan.FromMilliseconds(10);
            if (i % 10 == 9)
                rig.Session.Tick();
        }
        rig.Advance(1);
        await rig.Session.StopAsync(TestContext.Current.CancellationToken);
        clock.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Demorou {clock.Elapsed}.");
        Assert.True(allocated < 64L * 1024 * 1024, $"Alocou {allocated} bytes.");
        var samples = RecordingRig.ReadSamples(rig.AudioPath);
        Assert.InRange(samples.Length, 60 * 16000, 61 * 16000 + 1600);
        Assert.Contains(samples, s => Math.Abs((int)s) > 1000); // as duas fontes foram misturadas
    }
}
