using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tests.Doubles;
using Whisper.Recorder.Presentation.Recording;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V04RecordingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PermissionFailureReleasesDevicesAndAllowsSystemOnlyRetry()
    {
        await using var rig = new RecordingRig();
        using var vm = new RecordingViewModel(rig.Session, rig.Repository);
        rig.Capture.DeniedSource = AudioSourceKind.Microphone;
        await vm.StartAsync([RecordingRig.Loopback, RecordingRig.Microphone], Token);
        Assert.False(vm.IsRecording);
        Assert.Contains("privacidade do Windows", vm.Error);
        Assert.Empty(rig.Capture.ActiveSources);
        Assert.Equal(RecordingState.Interrupted, Assert.Single(vm.Recordings).State);
        Assert.Contains(RecordingWarning.CaptureFailed, rig.Session.Current!.Warnings);
        await vm.StartAsync([RecordingRig.Loopback], Token);
        Assert.True(vm.IsRecording);
        Assert.Null(vm.Error);
        rig.Advance(1);
        await vm.StopAsync(Token);
    }

    [Fact]
    public async Task LevelsAreAvailableBeforeRecordingStarts()
    {
        await using var rig = new RecordingRig();
        using var vm = new RecordingViewModel(rig.Session, rig.Repository);
        rig.Capture.EmitLevel(new AudioLevel(AudioSourceKind.Microphone, 0.7f));
        Assert.Equal(0.7f, vm.Levels[AudioSourceKind.Microphone]);
        Assert.False(vm.IsRecording);
    }

    [Fact]
    public async Task TwoSourcesAreAttenuatedAndMixedWithoutClipping()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, short.MaxValue);
        rig.Emit(AudioSourceKind.SystemLoopback, short.MaxValue);
        rig.Advance(1);
        await rig.Session.StopAsync(Token);
        var samples = rig.ReadSamples();
        Assert.Equal(16000, samples.Length);
        Assert.All(samples, s => Assert.Equal(32766, s));
        Assert.Equal(RecordingState.Saved, rig.Session.Current!.State);
        Assert.Empty(rig.Capture.ActiveSources);
    }

    [Fact]
    public async Task MissingLoopbackForSeveralMinutesInsertsSilenceAndPreservesWallDuration()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 8000);
        rig.Emit(AudioSourceKind.SystemLoopback, 4000);
        rig.Advance(1);
        rig.Emit(AudioSourceKind.Microphone, 6000);
        rig.Advance(180);
        await rig.Session.StopAsync(Token);
        var samples = rig.ReadSamples();
        Assert.Equal(181 * 16000, samples.Length);
        Assert.Equal(6000, samples[0]);
        Assert.Equal(3000, samples[16000]);
        Assert.All(samples.Skip(32000), s => Assert.Equal(0, s));
        Assert.Equal(TimeSpan.FromSeconds(181), rig.Session.Current!.Duration);
    }

    [Fact]
    public async Task SmallBlocksDeliveredAfterTheirPeriodAcrossTicksLeaveNoGaps()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        var origin = rig.Clock.UtcNow;
        // 10 ms blocks arrive when their period ends; ticks land mid-period (45 ms, 145 ms, ...).
        for (var step = 1; step <= 200; step++)
        {
            rig.Clock.UtcNow = origin + TimeSpan.FromMilliseconds(step * 5);
            if (step % 2 == 0)
            {
                var capturedAt = origin + TimeSpan.FromMilliseconds((step / 2 - 1) * 10);
                rig.Capture.EmitBlock(new AudioPcmBlock(AudioSourceKind.Microphone, capturedAt, 16000, 1, Enumerable.Repeat((short)8000, 160).ToArray()));
                rig.Capture.EmitBlock(new AudioPcmBlock(AudioSourceKind.SystemLoopback, capturedAt, 16000, 1, Enumerable.Repeat((short)4000, 160).ToArray()));
            }
            if (step % 20 == 9)
                rig.Session.Tick();
        }
        await rig.Session.StopAsync(Token);
        var samples = rig.ReadSamples();
        Assert.Equal(16000, samples.Length);
        Assert.All(samples, s => Assert.Equal(6000, s));
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Session.Current!.Duration);
    }

    [Fact]
    public async Task ClockProducesAudioEvenWhenNeitherSourceDeliversPackets()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Advance(3);
        await rig.Session.StopAsync(Token);
        Assert.Equal(48000, rig.ReadSamples().Length);
        Assert.All(rig.ReadSamples(), s => Assert.Equal(0, s));
    }

    [Theory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.SystemLoopback)]
    public async Task OneEnabledSourceExcludesPacketsFromTheOther(AudioSourceKind source)
    {
        await using var rig = new RecordingRig();
        await rig.Start(source);
        rig.Emit(AudioSourceKind.Microphone, 10000);
        rig.Emit(AudioSourceKind.SystemLoopback, 10000);
        rig.Advance(1);
        await rig.Session.StopAsync(Token);
        Assert.All(rig.ReadSamples(), s => Assert.Equal(5000, s));
        Assert.Equal(source, Assert.Single(rig.Session.Current!.Sources));
    }

    [Fact]
    public async Task BothSourcesOffExplainsWhyAndCreatesNoRecording()
    {
        await using var rig = new RecordingRig();
        using var vm = new RecordingViewModel(rig.Session, rig.Repository);
        await vm.StartAsync([], Token);
        Assert.Contains("ao menos uma fonte", vm.Error);
        Assert.False(vm.IsRecording);
        Assert.Empty(vm.Recordings);
        Assert.Empty(rig.Capture.ActiveSources);
    }

    [Fact]
    public async Task ResamplesStereo48000ToMono16000AndWritesCanonicalHeader()
    {
        await using var rig = new RecordingRig();
        await rig.Start(AudioSourceKind.Microphone);
        var stereo = Enumerable.Range(0, 48000).SelectMany(_ => new short[] { 6000, 2000 }).ToArray();
        rig.Capture.EmitBlock(new AudioPcmBlock(AudioSourceKind.Microphone, rig.Clock.UtcNow, 48000, 2, stereo));
        rig.Advance(1);
        await rig.Session.StopAsync(Token);
        Assert.All(rig.ReadSamples(), s => Assert.Equal(2000, s));
        var bytes = File.ReadAllBytes(rig.AudioPath);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
    }

    [Fact]
    public async Task SourceTimestampsPlaceSilenceBeforeDelayedSource()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 8000);
        rig.Emit(AudioSourceKind.SystemLoopback, 4000, rig.Clock.UtcNow.AddSeconds(1));
        rig.Advance(3);
        await rig.Session.StopAsync(Token);
        var samples = rig.ReadSamples();
        Assert.All(samples.Take(16000), s => Assert.Equal(4000, s));
        Assert.All(samples.Skip(16000).Take(16000), s => Assert.Equal(2000, s));
        Assert.All(samples.Skip(32000), s => Assert.Equal(0, s));
    }

    [Fact]
    public async Task PauseExcludesStoppedTimeAndPacketsIncludingLatePacketsAfterResume()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 4000);
        rig.Advance(1);
        rig.Session.Pause();
        var pausedAt = rig.Clock.UtcNow;
        rig.Emit(AudioSourceKind.Microphone, 30000);
        rig.Advance(120);
        Assert.True(rig.Session.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Session.Current!.Duration);
        rig.Session.Resume();
        rig.Emit(AudioSourceKind.Microphone, 30000, pausedAt);
        rig.Emit(AudioSourceKind.Microphone, 8000);
        rig.Advance(1);
        await rig.Session.StopAsync(Token);
        var samples = rig.ReadSamples();
        Assert.Equal(32000, samples.Length);
        Assert.All(samples.Take(16000), s => Assert.Equal(2000, s));
        Assert.All(samples.Skip(16000), s => Assert.Equal(4000, s));
    }

    [Fact]
    public async Task LostDeviceWarnsAndContinuesRecordingRemainingSource()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 4000);
        rig.Emit(AudioSourceKind.SystemLoopback, 8000);
        rig.Advance(1);
        rig.Capture.EmitAvailability(new AudioDeviceAvailability(RecordingRig.Microphone, false));
        rig.Emit(AudioSourceKind.Microphone, 30000);
        rig.Emit(AudioSourceKind.SystemLoopback, 8000);
        rig.Advance(1);
        Assert.True(rig.Session.IsRecording);
        await rig.Session.StopAsync(Token);
        Assert.Contains(RecordingWarning.DeviceLost, rig.Repository.List()[0].Warnings);
        Assert.False(rig.Session.SourceAvailability[AudioSourceKind.Microphone]);
        var samples = rig.ReadSamples();
        Assert.Equal(6000, samples[0]);
        Assert.All(samples.Skip(16000), s => Assert.Equal(4000, s));
    }

    [Fact]
    public async Task UnselectedDeviceLossDoesNotDisableSelectedMicrophone()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Capture.EmitAvailability(new AudioDeviceAvailability(RecordingRig.Microphone with { Id = "other" }, false));
        Assert.True(rig.Session.SourceAvailability[AudioSourceKind.Microphone]);
        Assert.Empty(rig.Session.Current!.Warnings);
    }

    [Fact]
    public async Task WarnsFiveMinutesBeforeLimitThenSavesExactlyTwoHours()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Advance(RecordingSession.LimitWarningAt.TotalSeconds - 1);
        Assert.DoesNotContain(RecordingWarning.LimitApproaching, rig.Session.Current!.Warnings);
        rig.Advance(1);
        Assert.Contains(RecordingWarning.LimitApproaching, rig.Session.Current!.Warnings);
        Assert.True(rig.Session.IsRecording);
        rig.Advance(600);
        Assert.False(rig.Session.IsRecording);
        Assert.Equal(RecordingSession.MaximumDuration, rig.Session.Current!.Duration);
        Assert.Equal(RecordingState.Saved, rig.Repository.List()[0].State);
        Assert.Contains(RecordingWarning.ServiceLimitReached, rig.Session.Current.Warnings);
        Assert.Equal(44 + 7200L * 32000, new FileInfo(rig.AudioPath).Length);
        await rig.Session.StopAsync(Token);
        Assert.Empty(rig.Capture.ActiveSources);
    }

    [Fact]
    public async Task LowDiskWarnsWithoutStoppingAndFullDiskSavesPersistedAudio()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 8000);
        rig.Disk.Bytes = RecordingSession.LowDiskBytes - 1;
        rig.Advance(1 + RecordingSession.JitterMargin.TotalSeconds);
        Assert.Contains(RecordingWarning.LowDiskSpace, rig.Session.Current!.Warnings);
        Assert.True(rig.Session.IsRecording);
        rig.Disk.Bytes = 0;
        rig.Advance(1);
        Assert.False(rig.Session.IsRecording);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Session.Current!.Duration);
        Assert.Contains(RecordingWarning.DiskFull, rig.Repository.List()[0].Warnings);
        Assert.Equal(RecordingState.Saved, rig.Session.Current.State);
        Assert.All(rig.ReadSamples(), s => Assert.Equal(4000, s));
        Assert.False(File.Exists(Path.Combine(rig.Repository.DirectoryFor(rig.Session.Current.Id), ".reserve")));
    }

    [Fact]
    public async Task EnospcDuringRecordingFinalizesExistingAudio()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Emit(AudioSourceKind.Microphone, 8000);
        rig.Advance(1 + RecordingSession.JitterMargin.TotalSeconds);
        rig.Disk.Failure = new IOException("Synthetic ENOSPC", 28);
        rig.Advance(1);
        Assert.False(rig.Session.IsRecording);
        Assert.Contains(RecordingWarning.DiskFull, rig.Session.Current!.Warnings);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Session.Current.Duration);
        Assert.Equal(RecordingState.Saved, Assert.Single(rig.Repository.List()).State);
        Assert.Equal(16000, rig.ReadSamples().Length);
        Assert.All(rig.ReadSamples(), s => Assert.Equal(4000, s));
    }

    [Fact]
    public async Task CheckpointsAudioAndAtomicMetadataBeforeStopAndListsNewestFirst()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        var first = rig.Session.Current!;
        rig.Advance(2 + RecordingSession.JitterMargin.TotalSeconds);
        var live = rig.Repository.List()[0];
        Assert.Equal(RecordingState.Recording, live.State);
        Assert.Equal(TimeSpan.FromSeconds(2) + RecordingSession.JitterMargin, live.Duration);
        Assert.Equal(32000, rig.ReadSamples().Length);
        Assert.False(File.Exists(Path.Combine(rig.Repository.DirectoryFor(live.Id), "metadata.json.tmp")));
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Repository.DirectoryFor(live.Id), "metadata.json"))))
            Assert.Equal("recording", json.RootElement.GetProperty("State").GetString());
        await rig.Session.StopAsync(Token);
        rig.Advance(60);
        await rig.Start(AudioSourceKind.SystemLoopback);
        rig.Advance(1);
        await rig.Session.StopAsync(Token);
        var list = rig.Repository.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(first.Id, list[1].Id);
        Assert.Equal(rig.Session.Current!.Id, list[0].Id);
        Assert.False(string.IsNullOrWhiteSpace(list[0].Title));
        Assert.Equal(RecordingState.Saved, list[0].State);
        Assert.Equal(TimeSpan.FromSeconds(1), list[0].Duration);
    }

    [Fact]
    public async Task ViewModelExposesLevelsSourcesCounterAndNoticeOnEveryStart()
    {
        await using var rig = new RecordingRig();
        using var vm = new RecordingViewModel(rig.Session, rig.Repository);
        var changes = 0;
        vm.PropertyChanged += (_, _) => changes++;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await vm.StartAsync([RecordingRig.Microphone, RecordingRig.Loopback], Token);
            Assert.True(vm.IsRecording);
            Assert.NotNull(vm.ThirdPartyNotice);
            Assert.Null(vm.Error);
            rig.Capture.EmitLevel(new AudioLevel(AudioSourceKind.Microphone, 0.8f));
            rig.Capture.EmitLevel(new AudioLevel(AudioSourceKind.SystemLoopback, 0.2f));
            Assert.Equal(0.8f, vm.Levels[AudioSourceKind.Microphone]);
            Assert.Equal(0.2f, vm.Levels[AudioSourceKind.SystemLoopback]);
            Assert.True(vm.Sources[AudioSourceKind.SystemLoopback]);
            rig.Advance(1);
            Assert.Equal(TimeSpan.FromSeconds(1), vm.Duration);
            vm.Pause();
            Assert.True(vm.IsPaused);
            vm.Resume();
            await vm.StopAsync(Token);
            Assert.Null(vm.ThirdPartyNotice);
            Assert.Equal(RecordingState.Saved, vm.State);
        }
        Assert.True(changes > 0);
        Assert.Equal(2, vm.Recordings.Count);
    }

    [Fact]
    public async Task SchedulerDrivesRecordingWithoutHostTicks()
    {
        await using var rig = new RecordingRig();
        await rig.Start();
        rig.Clock.UtcNow += TimeSpan.FromSeconds(1);
        rig.Scheduler.Pulse();
        await rig.Scheduler.WaitForNextDelay(Token);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Session.Current!.Duration);
    }

    [Fact]
    public async Task KillRecordingProcessThenRecoverRepairsHeaderAndIncompleteSample()
    {
        await using var rig = new RecordingRig();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--filter-class");
        start.ArgumentList.Add("*RecordingCrashWorker*");
        start.ArgumentList.Add("--minimum-expected-tests");
        start.ArgumentList.Add("1");
        start.Environment["WHISPER_V04_CRASH_DIRECTORY"] = rig.Repository.RootDirectory;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(rig.Repository.RootDirectory, "ready")))
            {
                Assert.False(process.HasExited, process.HasExited ? await stderr : "");
                Assert.True(DateTime.UtcNow < deadline, "Recording worker did not checkpoint within 30 seconds.");
                await Task.Delay(25, Token);
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(Token);
            await stdout;
            await stderr;
        }
        var metadata = Assert.Single(rig.Repository.List());
        Assert.Equal(RecordingState.Recording, metadata.State);
        var path = rig.Repository.AudioPath(metadata.Id);
        // Model a final torn write after the last header checkpoint.
        using (var file = new FileStream(path, FileMode.Append))
            file.Write(new byte[] { 0x10, 0x27, 0xff });
        var recovered = Assert.Single(new RecordingRepository(rig.Repository.RootDirectory).RecoverInterrupted());
        Assert.Equal(RecordingState.Interrupted, recovered.State);
        Assert.Equal(TimeSpan.FromSeconds(2 + 1d / 16000), recovered.Duration);
        var samples = RecordingRig.ReadSamples(path);
        Assert.Equal(32001, samples.Length);
        Assert.Equal(10000, samples[^1]);
        Assert.All(samples.Take(32000), s => Assert.Equal(4000, s));
        var recoveredAgain = Assert.Single(rig.Repository.RecoverInterrupted());
        Assert.Equal(recovered.Id, recoveredAgain.Id);
        Assert.Equal(recovered.Duration, recoveredAgain.Duration);
        Assert.Equal(RecordingState.Interrupted, recoveredAgain.State);
    }
}

public sealed class RecordingCrashWorker
{
    [Fact]
    public async Task RecordUntilParentKillsProcess()
    {
        var directory = Environment.GetEnvironmentVariable("WHISPER_V04_CRASH_DIRECTORY");
        if (directory is null)
            return;
        await using var rig = new RecordingRig(directory);
        await rig.Start(AudioSourceKind.Microphone);
        for (var i = 0; i < 2; i++)
        {
            rig.Emit(AudioSourceKind.Microphone, 8000);
            rig.Advance(1);
        }
        rig.Advance(RecordingSession.JitterMargin.TotalSeconds);
        File.WriteAllText(Path.Combine(directory, "ready"), "checkpointed");
        await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
    }
}

internal sealed class RecordingRig : IAsyncDisposable
{
    public static readonly AudioDevice Microphone = new("mic", "Microphone", AudioSourceKind.Microphone, true);
    public static readonly AudioDevice Loopback = new("system", "System", AudioSourceKind.SystemLoopback, true);
    public FakeAudioCapture Capture { get; } = new();
    public FakeClock Clock { get; } = new() { UtcNow = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    public ControlledScheduler Scheduler { get; } = new();
    public TestDiskSpace Disk { get; } = new();
    public RecordingRepository Repository { get; }
    public RecordingSession Session { get; }
    public string AudioPath => Repository.AudioPath(Session.Current!.Id);

    public RecordingRig(string? directory = null)
    {
        Repository = new RecordingRepository(directory ?? Path.Combine(Path.GetTempPath(), "whisper-v04-" + Guid.NewGuid()));
        Session = new RecordingSession(Capture, Clock, Scheduler, Repository, Disk);
    }

    public Task Start(params AudioSourceKind[] sources) => Session.StartAsync(
        (sources.Length == 0 ? new[] { Microphone, Loopback } : sources.Select(s => s == AudioSourceKind.Microphone ? Microphone : Loopback).ToArray()),
        TestContext.Current.CancellationToken);

    public void Emit(AudioSourceKind source, short sample, DateTimeOffset? at = null) =>
        Capture.EmitBlock(new AudioPcmBlock(source, at ?? Clock.UtcNow, 16000, 1, Enumerable.Repeat(sample, 16000).ToArray()));

    public void Advance(double seconds)
    {
        Clock.UtcNow += TimeSpan.FromSeconds(seconds);
        Session.Tick();
    }

    public short[] ReadSamples() => ReadSamples(AudioPath);
    public static short[] ReadSamples(string path)
    {
        // ReadWrite: no Windows, a gravação ativa mantém o arquivo aberto para escrita.
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var copy = new MemoryStream())
        {
            file.CopyTo(copy);
            bytes = copy.ToArray();
        }
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(bytes.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(0, (bytes.Length - 44) % 2);
        return Enumerable.Range(0, (bytes.Length - 44) / 2)
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2))).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await Capture.DisposeAsync();
        Directory.Delete(Repository.RootDirectory, recursive: true);
    }
}

internal sealed class TestDiskSpace : IDiskSpace
{
    public long Bytes { get; set; } = long.MaxValue;
    public IOException? Failure { get; set; }
    public long AvailableBytes(string directory) => Failure is not null ? throw Failure : Bytes;
}

internal sealed class ControlledScheduler : IScheduler
{
    private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        requested.TrySetResult();
        return new ValueTask(next.Task.WaitAsync(cancellationToken));
    }

    public void Pulse()
    {
        var previous = next;
        next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    public Task WaitForNextDelay(CancellationToken cancellationToken) => requested.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
}
