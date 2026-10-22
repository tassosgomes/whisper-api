using Whisper.Recorder.Core.Recording;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

public sealed class V04StopFailureTests
{
    [Fact]
    public async Task FailureStoppingTheDeviceStillPublishesTheSavedRecordingAndRethrows()
    {
        var rig = new RecordingRig();
        try
        {
            await rig.Start();
            rig.Emit(Ports.AudioSourceKind.Microphone, 1000);
            rig.Advance(1);
            var changes = 0;
            rig.Session.Changed += () => changes++;
            rig.Capture.StopFailure = new IOException("dispositivo não parou");

            // A exceção do dispositivo continua chegando a quem chamou, como antes.
            await Assert.ThrowsAsync<IOException>(() => rig.Session.StopAsync(TestContext.Current.CancellationToken));

            // Mas o instantâneo não fica preso em "gravando": a gravação foi salva.
            Assert.False(rig.Session.IsRecording);
            Assert.False(rig.Session.Snapshot.IsRecording);
            Assert.Equal(RecordingState.Saved, rig.Session.Current!.State);
            Assert.Equal(RecordingState.Saved, rig.Repository.Read(rig.Session.Current.Id).State);
            Assert.True(changes > 0);
            Assert.All(rig.Session.Levels.Values, level => Assert.Equal(0, level));
            Assert.Equal(16000, RecordingRig.ReadSamples(rig.AudioPath).Length);
        }
        finally
        {
            rig.Capture.StopFailure = null;
            try { await rig.DisposeAsync(); }
            catch (IOException) { } // o temporizador com falha relança ao descartar
            try { Directory.Delete(rig.Repository.RootDirectory, recursive: true); }
            catch (IOException) { }
        }
    }
}
