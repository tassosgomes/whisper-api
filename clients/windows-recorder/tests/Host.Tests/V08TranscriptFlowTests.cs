using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Transcripts;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Transcripts;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Da lista à transcrição (V-08, host): o acompanhamento salva o resultado, o toque na notificação seleciona a gravação, a
/// lista oferece abrir a transcrição e a leitura funciona com o serviço indisponível. A API é um servidor em memória.
/// </summary>
public sealed class V08TranscriptFlowTests
{
    [Fact]
    public async Task NotificationLeadsToTheTranscriptAndItReadsWithTheServiceUnavailable()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var id = rig.Seed(RecordingState.Processing, "trn_v08", title: "Reunião semanal");
        rig.Api.Job("trn_v08", "completed");
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.StartAsync();
        await rig.WaitForStatusAsync(id, "Transcrita");

        // O serviço some: a leitura depende só dos arquivos guardados no PC.
        rig.Api.NetworkDown = true;
        var requestsBefore = rig.Api.Requests;
        var requested = new List<Guid>();
        rig.Flow.TranscriptRequested += requested.Add;
        var clipboard = new FakeClipboard();
        var screen = await rig.Ui.InvokeAsync(() => new TranscriptScreenViewModel(
            new TranscriptViewModel(new LocalTranscriptStore(rig.Repository)), clipboard, new FakeDialogs()));

        await rig.Ui.InvokeAsync(() =>
        {
            rig.Activation.Activate(id);
            Assert.Equal(id, rig.Flow.SelectedRecordingId);
            Assert.True(rig.Flow.CanOpenTranscript);
            Assert.True(rig.Flow.OpenTranscript.CanExecute(null));
            rig.Flow.OpenTranscript.Execute(null);
        });
        Assert.Equal(new[] { id }, requested);

        await rig.Ui.InvokeAsync(async () =>
        {
            await screen.OpenAsync(requested[0]);
            await screen.CopyWithTimes.ExecuteAsync();
        });

        Assert.Equal("Reunião semanal", screen.Title);
        Assert.Equal("Olá reunião", Assert.Single(screen.Lines).Text);
        Assert.Equal("[00:00:01.240] Olá reunião" + Environment.NewLine, Assert.Single(clipboard.Copied));
        Assert.Equal(requestsBefore, rig.Api.Requests);
    }

    [Fact]
    public async Task OnlyATranscribedRecordingOffersTheTranscript()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var saved = rig.Seed(RecordingState.Saved, title: "Salva");
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(1);

        await rig.Ui.InvokeAsync(() =>
        {
            rig.Flow.SelectRecording(saved);
            Assert.False(rig.Flow.CanOpenTranscript);
            Assert.False(rig.Flow.OpenTranscript.CanExecute(null));
        });
    }
}
