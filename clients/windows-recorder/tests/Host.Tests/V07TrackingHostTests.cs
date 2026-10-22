using System.ComponentModel;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Host.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Notificação ao concluir ou falhar com o app minimizado, e o toque nela (V-07, host). A API é um servidor em memória; o
/// Windows é um dublê que só guarda o que seria exibido. Nada toca a rede, o cofre real nem a pasta do app.
/// </summary>
public sealed class V07NotificationTests
{
    [Fact]
    public async Task CompletionAndFailureNotifyWhileTheWindowIsMinimizedAndNameTheRecording()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var done = rig.Seed(RecordingState.Processing, "trn_ok");
        var broken = rig.Seed(RecordingState.Processing, "trn_bad");
        rig.Api.Job("trn_ok", "completed");
        rig.Api.Job("trn_bad", "failed", "UNSUPPORTED_MEDIA_FORMAT");

        await rig.StartAsync();

        // O núcleo decidiu notificar; a janela continua minimizada (nenhum pedido para mostrá-la).
        await TrackingRig.Eventually(() => rig.Toast.Shown.Count == 2);
        Assert.Equal(0, rig.WindowShown);
        Assert.Contains((RecorderNotification.TranscriptionCompleted, (Guid?)done), rig.Toast.Shown);
        Assert.Contains((RecorderNotification.TranscriptionFailed, (Guid?)broken), rig.Toast.Shown);
        Assert.Equal("Transcrita", (await rig.WaitForStatusAsync(done, "Transcrita")).Status);
        Assert.Equal("Falhou", (await rig.WaitForStatusAsync(broken, "Falhou")).Status);

        // Uma nova passagem não repete o aviso.
        rig.Clock.UtcNow += TimeSpan.FromMinutes(5);
        await rig.PassAsync();
        Assert.Equal(2, rig.Toast.Shown.Count);
    }

    [Fact]
    public async Task TappingTheNotificationShowsTheWindowAndSelectsTheRecording()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var first = rig.Seed(RecordingState.Saved, title: "Primeira");
        var second = rig.Seed(RecordingState.Processing, "trn_2", title: "Segunda");
        rig.Api.Job("trn_2", "completed");
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(2);
        var requested = new List<Guid>();
        rig.Flow.SelectionRequested += requested.Add;

        await rig.StartAsync();
        await TrackingRig.Eventually(() => rig.Toast.Shown.Count == 1);
        var tapped = rig.Toast.Shown[0].Recording;
        Assert.Equal(second, tapped);

        await rig.Ui.InvokeAsync(() => rig.Activation.Activate(tapped));

        Assert.Equal(1, rig.WindowShown);
        Assert.Equal(second, rig.Flow.SelectedRecordingId);
        Assert.Equal("Segunda", rig.Flow.SelectedRecording?.Title);
        Assert.Equal(new[] { second }, requested);

        // Um aviso sem gravação (ex.: dispositivo perdido) só abre a janela e mantém a escolha.
        await rig.Ui.InvokeAsync(() => rig.Activation.Activate(null));
        Assert.Equal(2, rig.WindowShown);
        Assert.Equal(second, rig.Flow.SelectedRecordingId);
        Assert.NotEqual(first, rig.Flow.SelectedRecordingId);
    }
}

/// <summary>A lista mostra a situação do job, as mensagens de falha e de expiração, e troca só a linha afetada.</summary>
public sealed class V07ListTests
{
    [Fact]
    public async Task TheListUpdatesOneRowWithoutReloadingTheOthersFromDisk()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var untouchedA = rig.Seed(RecordingState.Saved, title: "A");
        var untouchedB = rig.Seed(RecordingState.Saved, title: "B");
        var followed = rig.Seed(RecordingState.Processing, "trn_1", title: "C");
        rig.Api.Job("trn_1", "processing");
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(3);
        var before = await rig.ListAsync();
        var a = before.Single(r => r.Id == untouchedA);
        var b = before.Single(r => r.Id == untouchedB);
        // Se alguma passagem relesse o disco, essas duas gravações sumiriam da lista.
        Directory.Delete(rig.Repository.DirectoryFor(untouchedA), recursive: true);
        Directory.Delete(rig.Repository.DirectoryFor(untouchedB), recursive: true);

        await rig.StartAsync();
        var processing = await rig.WaitForStatusAsync(followed, "Processando");
        Assert.Contains("processamento", processing.Detail);

        rig.Api.Job("trn_1", "completed");
        rig.Clock.UtcNow += TimeSpan.FromMinutes(5);
        await rig.PassAsync();
        var transcribed = await rig.WaitForStatusAsync(followed, "Transcrita");

        Assert.Null(transcribed.Detail);
        var after = await rig.ListAsync();
        Assert.Equal(3, after.Count);
        Assert.Same(a, after.Single(r => r.Id == untouchedA));
        Assert.Same(b, after.Single(r => r.Id == untouchedB));
    }

    [Fact]
    public async Task ManyStatusEventsBecomeAFewUiUpdatesWithoutReadingTheDisk()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var id = rig.Seed(RecordingState.Processing, "trn_1");
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(1);
        var metadata = rig.Repository.Read(id);
        // O disco some: um evento que relesse a lista deixaria a lista vazia.
        Directory.Delete(rig.Repository.DirectoryFor(id), recursive: true);
        var changes = 0;
        PropertyChangedEventHandler count = (_, e) =>
        {
            if (e.PropertyName == nameof(Presentation.Recording.RecordingScreenViewModel.Recordings))
                Interlocked.Increment(ref changes);
        };
        rig.Screen.PropertyChanged += count;

        await Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                rig.Screen.ApplyTracking(new TrackingStatus(metadata with { PublicJobStatus = i % 2 == 0 ? "queued" : "processing" }, false));
            rig.Screen.ApplyTracking(new TrackingStatus(metadata with { PublicJobStatus = "queued" }, false));
        }, TestContext.Current.CancellationToken);

        await rig.WaitForDetailAsync(id, "Transcrição na fila.");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal("Transcrição na fila.", (await rig.ItemAsync(id)).Detail);
        Assert.InRange(changes, 1, 3);
        rig.Screen.PropertyChanged -= count;
    }

    [Fact]
    public async Task FailureAndExpiredResultShowTheMessageAndAllowSendingAgain()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var failed = rig.Seed(RecordingState.Processing, "trn_bad");
        var expired = rig.Seed(RecordingState.Processing, "trn_gone");
        rig.Api.Job("trn_bad", "failed", "UNSUPPORTED_MEDIA_FORMAT");
        rig.Api.Expire("trn_gone");

        await rig.StartAsync();

        var failure = await rig.WaitForStatusAsync(failed, "Falhou");
        Assert.Contains("formato do áudio", failure.Detail);
        Assert.Contains("enviar de novo", failure.Detail);
        Assert.True(failure.CanTranscribe);
        var gone = await rig.WaitForStatusAsync(expired, "Resultado expirado");
        Assert.Contains("expirou", gone.Detail);
        Assert.Contains("PC", gone.Detail);
        Assert.True(gone.CanTranscribe);
        Assert.Equal(RecordingState.ResultExpired, rig.Repository.Read(expired).State);
    }
}

/// <summary>Sem conexão é um aviso: a gravação continua em processamento e nada vira falha.</summary>
public sealed class V07OfflineTests
{
    [Fact]
    public async Task NoConnectionShowsTheNoticeWithoutFailingAndResumesWhenTheNetworkReturns()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var id = rig.Seed(RecordingState.Processing, "trn_1");
        rig.Api.Job("trn_1", "completed");
        rig.Connectivity.IsConnected = false;
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(1);

        await rig.StartAsync();

        await rig.WaitForOfflineAsync(id);
        var offline = await rig.ItemAsync(id);
        Assert.Equal("Processando", offline.Status);
        Assert.Contains("Sem conexão", offline.Detail);
        Assert.DoesNotContain("falh", offline.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordingState.Processing, rig.Repository.Read(id).State);
        Assert.Equal(0, rig.Api.Requests);
        Assert.Empty(rig.Toast.Shown);

        // Uma releitura do disco (outro evento da tela) não apaga o aviso.
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True((await rig.ItemAsync(id)).IsOffline);

        rig.Connectivity.IsConnected = true;
        await rig.PassAsync();
        await rig.WaitForStatusAsync(id, "Transcrita");
        Assert.False((await rig.ItemAsync(id)).IsOffline);
        Assert.Equal(new[] { (RecorderNotification.TranscriptionCompleted, (Guid?)id) }, rig.Toast.Shown);
    }

    [Fact]
    public async Task ARequestThatCannotReachTheServiceKeepsProcessingAndNeverBecomesAFailure()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var id = rig.Seed(RecordingState.Processing, "trn_1");
        rig.Api.Job("trn_1", "completed");
        rig.Api.NetworkDown = true;
        await rig.Ui.InvokeAsync(() => rig.Screen.RefreshRecordings());
        await rig.WaitForCountAsync(1);

        await rig.StartAsync();

        await rig.WaitForOfflineAsync(id);
        var item = await rig.ItemAsync(id);
        Assert.Equal("Processando", item.Status);
        Assert.Contains("Sem conexão", item.Detail);
        var stored = rig.Repository.Read(id);
        Assert.Equal(RecordingState.Processing, stored.State);
        Assert.Null(stored.FailureCode);
        Assert.Empty(rig.Toast.Shown);

        rig.Api.NetworkDown = false;
        rig.Clock.UtcNow += TimeSpan.FromMinutes(5);
        await rig.PassAsync();
        await rig.WaitForStatusAsync(id, "Transcrita");
        Assert.Single(rig.Toast.Shown);
    }
}

/// <summary>Início na abertura, retomada depois de fechar o app e reinício do acompanhamento.</summary>
public sealed class V07LifecycleTests
{
    [Fact]
    public async Task ReopeningTheAppResumesAJobLeftProcessingAndShowsItTranscribed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "whisper-host-v07-reopen-" + Guid.NewGuid().ToString("N"));
        Guid id;
        var start = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await using (var closed = await TrackingRig.CreateAsync(directory, keepDirectory: true, now: start))
            {
                id = closed.Seed(RecordingState.Processing, "trn_1");
                closed.Api.Job("trn_1", "processing");
                await closed.StartAsync();
                await closed.WaitForStatusAsync(id, "Processando");
                // O app é fechado aqui, com o job ainda em processamento.
            }
            Assert.Equal(RecordingState.Processing, new RecordingRepository(Path.Combine(directory, "recordings")).Read(id).State);

            await using var reopened = await TrackingRig.CreateAsync(directory, now: start.AddHours(1));
            reopened.Api.Job("trn_1", "completed");
            Assert.Equal("Processando", (await reopened.ItemAsync(id)).Status);

            await reopened.StartAsync();

            await reopened.WaitForStatusAsync(id, "Transcrita");
            Assert.Equal(new[] { (RecorderNotification.TranscriptionCompleted, (Guid?)id) }, reopened.Toast.Shown);
            Assert.Contains("Olá reunião", File.ReadAllText(Path.Combine(reopened.Repository.DirectoryFor(id), "transcription.txt")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task AnUnexpectedErrorRestartsTheTrackingAndStoppingItEndsTheLoop()
    {
        await using var rig = await TrackingRig.CreateAsync();
        var id = rig.Seed(RecordingState.Processing, "trn_1");
        rig.Api.Job("trn_1", "processing");
        rig.Scheduler.FaultNext();

        await rig.StartAsync();
        await TrackingRig.Eventually(() => rig.Tracking.Restarts == 1 && rig.Scheduler.RestartWaits == 1);
        var requests = rig.Api.Requests;

        // Um novo envio aceito acorda o acompanhamento sem esperar a pausa.
        rig.Tracking.Wake();
        await TrackingRig.Eventually(() => rig.Scheduler.Waiting >= 2);
        Assert.Equal(1, rig.Tracking.Restarts);
        Assert.Equal(RecordingState.Processing, rig.Repository.Read(id).State);

        rig.Tracking.Stop();
        rig.Api.Job("trn_1", "completed");
        rig.Clock.UtcNow += TimeSpan.FromMinutes(5);
        rig.Scheduler.Tick();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(requests, rig.Api.Requests);
        Assert.Empty(rig.Toast.Shown);
    }
}
