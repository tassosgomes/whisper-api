using System.Text.Json;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Whisper.Recorder.Core.Api.Generated;
using Whisper.Recorder.Core.Api.Generated.Models;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task FoundationRunsWithoutWindowsOrNetwork()
    {
        var clock = new FakeClock();
        var scheduler = new FakeScheduler();
        using var diagnosticOutput = new StringWriter();
        var diagnostic = new DiagnosticLog(diagnosticOutput, clock);
        diagnostic.Write(DiagnosticEvent.ApplicationStarted);
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00 ApplicationStarted" + Environment.NewLine,
            diagnosticOutput.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostic.Write((DiagnosticEvent)(-1)));

        await using IAudioCapture capture = new FakeAudioCapture();
        var fakeCapture = Assert.IsType<FakeAudioCapture>(capture);
        var device = new AudioDevice("test-microphone", "Test microphone", AudioSourceKind.Microphone, true);
        fakeCapture.Devices.Add(device);
        Assert.Equal(device, Assert.Single(await capture.ListDevicesAsync(AudioSourceKind.Microphone, TestContext.Current.CancellationToken)));
        AudioPcmBlock? receivedBlock = null;
        AudioLevel? receivedLevel = null;
        AudioDeviceAvailability? receivedAvailability = null;
        capture.BlockAvailable += block => receivedBlock = block;
        capture.LevelChanged += level => receivedLevel = level;
        capture.DeviceAvailabilityChanged += availability => receivedAvailability = availability;
        await capture.StartAsync(device, TestContext.Current.CancellationToken);
        var block = new AudioPcmBlock(device.Source, clock.UtcNow, 16000, 1, new short[] { 0, 42 });
        fakeCapture.EmitBlock(block);
        fakeCapture.EmitLevel(new AudioLevel(device.Source, 0.5f));
        fakeCapture.EmitAvailability(new AudioDeviceAvailability(device, false));
        Assert.Same(block, receivedBlock);
        Assert.Equal(0.5f, receivedLevel?.Peak);
        Assert.False(receivedAvailability?.IsAvailable);
        await capture.StopAsync(device.Source, TestContext.Current.CancellationToken);
        Assert.Empty(fakeCapture.ActiveSources);

        ICredentialVault vault = new FakeCredentialVault();
        await vault.SaveAsync("test-only-key", TestContext.Current.CancellationToken);
        Assert.Equal("test-only-key", await vault.ReadAsync(TestContext.Current.CancellationToken));
        await vault.RemoveAsync(TestContext.Current.CancellationToken);
        Assert.Null(await vault.ReadAsync(TestContext.Current.CancellationToken));
        var notifier = new FakeNotifier();
        await notifier.SetRecordingIndicatorAsync(true, TestContext.Current.CancellationToken);
        await notifier.NotifyAsync(RecorderNotification.TranscriptionCompleted, TestContext.Current.CancellationToken);
        Assert.True(notifier.IsRecording);
        Assert.Equal(RecorderNotification.TranscriptionCompleted, Assert.Single(notifier.Notifications));
        await using IAudioPlayer player = new FakeAudioPlayer();
        await player.OpenAsync("test-only.wav", TestContext.Current.CancellationToken);
        await player.PlayAsync(TestContext.Current.CancellationToken);
        await player.SeekAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(player.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        await player.PauseAsync(TestContext.Current.CancellationToken);
        Assert.False(player.IsPlaying);
        await player.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.Zero, player.Position);
        await scheduler.DelayAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(scheduler.RequestedDelays));

        // Build and serialize the generated API request without sending it.
        using var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider());
        adapter.BaseUrl = "https://api.example.test/v1";
        var api = new WhisperApiClient(adapter);
        var request = api.TranscriptionUploads.ToPostRequestInformation(new CreateTranscriptionUploadRequest
        {
            ContentType = ContentType.AudioWav,
            SizeBytes = 5L * 1024 * 1024 * 1024,
            ChecksumSha256 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="
        }, config => config.Headers.Add("Idempotency-Key", "smoke-only"));
        Assert.Equal("https://api.example.test/v1/transcription-uploads", request.URI.AbsoluteUri);
        using var payload = await JsonDocument.ParseAsync(Assert.IsAssignableFrom<Stream>(request.Content), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5368709120L, payload.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("audio/wav", payload.RootElement.GetProperty("contentType").GetString());
    }
}
