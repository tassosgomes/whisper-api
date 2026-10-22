using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Access;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Recording;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Gestão na tela (V-09, host): renomear, apagar com e sem envio em andamento, apagar tudo, a trava durante a limpeza e a
/// liberação do arquivo aberto no player. O núcleo, o repositório e o diário são os reais, em pasta temporária. O cofre do
/// "apagar tudo" é o Credential Manager real, mas só com um alvo de teste (<c>Whisper.Recorder.Test/...</c>) removido ao fim;
/// o alvo de produção do app nunca é lido nem escrito. Nenhum teste usa a rede, os alto-falantes ou a pasta real do app.
/// </summary>
public sealed class V09ManagementFlowTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    static V09ManagementFlowTests() => StaleTestCredentials.SweepOnce();

    private readonly string target = "Whisper.Recorder.Test/ApiKey/V09/" + Guid.NewGuid().ToString("N");

    public void Dispose() => new WindowsCredentialVault(target).RemoveAsync().AsTask().GetAwaiter().GetResult();

    private static Task RunAsync(ManagementRig m, Func<AsyncRelayCommand> command) =>
        m.Rig.Ui.InvokeAsync(() => command().ExecuteAsync());

    private static Task<IReadOnlyList<RecordingListItem>> ListAsync(ManagementRig m) => m.Rig.ListAsync();

    [Fact]
    public async Task RenameSavesTheTitleEmptyReturnsToTheDefaultAndCancelChangesNothing()
    {
        await using var m = await ManagementRig.CreateAsync();
        var id = await m.SeedSelectedAsync(RecordingState.Saved, "Original", seconds: 5);

        m.Dialogs.NewTitle = "  Planejamento Q4  ";
        await RunAsync(m, () => m.Management.Rename);
        Assert.Equal("Original", Assert.Single(m.Dialogs.Prompts));
        Assert.Equal("Planejamento Q4", m.Rig.Repository.Read(id).Title);
        await TrackingRig.Eventually(() => ListAsync(m).GetAwaiter().GetResult().Single().Title == "Planejamento Q4");
        Assert.Equal("Título salvo.", m.Management.Message);

        // Vazio volta ao padrão: data e hora de criação, também na lista.
        m.Dialogs.NewTitle = "";
        await RunAsync(m, () => m.Management.Rename);
        var created = m.Rig.Repository.Read(id).CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal("", m.Rig.Repository.Read(id).Title);
        await TrackingRig.Eventually(() => ListAsync(m).GetAwaiter().GetResult().Single().Title == created);

        m.Dialogs.NewTitle = null;
        await RunAsync(m, () => m.Management.Rename);
        Assert.Equal("", m.Rig.Repository.Read(id).Title);
        Assert.Equal("Nenhuma alteração.", m.Management.Message);
        Assert.True(File.Exists(m.Rig.Repository.AudioPath(id)));
    }

    [Fact]
    public async Task DeleteAsksOnceRemovesTheRecordingAndUpdatesTheList()
    {
        await using var m = await ManagementRig.CreateAsync();
        var keep = m.Rig.Seed(RecordingState.Saved, title: "Outra");
        var id = await m.SeedSelectedAsync(RecordingState.Saved, "Apagar esta", seconds: 5);

        // "Não" no diálogo não apaga nada.
        m.Dialogs.ConfirmDelete = false;
        await RunAsync(m, () => m.Management.Delete);
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.Equal("Nenhum dado foi apagado.", m.Management.Message);

        m.Dialogs.ConfirmDelete = true;
        await RunAsync(m, () => m.Management.Delete);

        Assert.Equal(2, m.Dialogs.Deletes.Count);
        Assert.Empty(m.Dialogs.Pendings); // sem envio em andamento: nenhum aviso extra
        Assert.False(m.Dialogs.Deletes[^1].HasPendingTranscription);
        Assert.Contains("Não é possível desfazer", m.Dialogs.Deletes[^1].Message);
        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(keep)));
        Assert.Equal("Gravação apagada do PC.", m.Management.Message);
        await TrackingRig.Eventually(() => ListAsync(m).GetAwaiter().GetResult().Select(r => r.Id).SequenceEqual([keep]));
        Assert.False(m.Rig.Gate.IsBusy);
    }

    [Fact]
    public async Task DeleteWhileProcessingWarnsSeparatelyAndNeverCallsTheService()
    {
        await using var m = await ManagementRig.CreateAsync();
        var id = await m.SeedSelectedAsync(RecordingState.Processing, "Em processamento", seconds: 5, jobId: "trn_v09");
        m.Rig.Api.Job("trn_v09", "processing");
        var requests = m.Rig.Api.Requests;

        // Recusar o aviso específico mantém tudo, mesmo depois de aceitar o primeiro diálogo.
        m.Dialogs.ConfirmPending = false;
        await RunAsync(m, () => m.Management.Delete);
        Assert.Single(m.Dialogs.Pendings);
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.Empty(m.Host.Calls);

        m.Dialogs.ConfirmPending = true;
        await RunAsync(m, () => m.Management.Delete);

        Assert.Equal(2, m.Dialogs.Pendings.Count);
        var warning = m.Dialogs.Pendings[^1];
        Assert.True(warning.HasPendingTranscription);
        Assert.Contains("não cancela o job", warning.Message);
        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        // Só a cópia local sai: nenhuma chamada ao serviço.
        Assert.Equal(requests, m.Rig.Api.Requests);
    }

    [Fact]
    public async Task TheAudioOpenInThePlayerAndTheTranscriptAreReleasedBeforeDeleting()
    {
        await using var m = await ManagementRig.CreateAsync();
        var other = await m.SeedSelectedAsync(RecordingState.Saved, "Outra", seconds: 5);
        var id = await m.SeedSelectedAsync(RecordingState.Transcribed, "Aberta no player", seconds: 20);

        await m.Rig.Ui.InvokeAsync(async () =>
        {
            await m.Player.OpenAsync(id);
            await m.Player.PlayPause.ExecuteAsync();
        });
        m.Host.OpenTranscript = id;
        // Prova de que o arquivo realmente está preso pelo player.
        Assert.Throws<IOException>(() => File.Delete(m.Rig.Repository.AudioPath(id)));

        await RunAsync(m, () => m.Management.Delete);

        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.Equal(["release:" + id], m.Host.Calls);
        Assert.Equal([id], m.Host.ClosedTranscripts);
        Assert.Null(m.Player.RecordingId);
        Assert.True(m.Outputs.Latest.Disposed);
        Assert.Equal("Gravação apagada do PC.", m.Management.Message);
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(other)));
    }

    [Fact]
    public async Task APlayerOpenOnAnotherRecordingIsNotTouched()
    {
        await using var m = await ManagementRig.CreateAsync();
        var playing = m.Rig.Seed(RecordingState.Saved, title: "Tocando");
        SyntheticWav.Write(m.Rig.Repository.AudioPath(playing), TimeSpan.FromSeconds(10));
        var id = await m.SeedSelectedAsync(RecordingState.Saved, "Apagar", seconds: 5);
        await m.Rig.Ui.InvokeAsync(() => m.Player.OpenAsync(playing));

        await RunAsync(m, () => m.Management.Delete);

        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.Equal(playing, m.Player.RecordingId);
        Assert.True(m.Player.IsOpen);
    }

    [Fact]
    public async Task DeleteAllEmptiesTheFolderRemovesTheKeyFromTheTestVaultAndRefreshesTheState()
    {
        var vault = new WindowsCredentialVault(target);
        await using var m = await ManagementRig.CreateAsync(vault);
        await m.SeedSelectedAsync(RecordingState.Saved, "Uma", seconds: 5);
        await m.SeedSelectedAsync(RecordingState.Transcribed, "Duas", seconds: 5);
        Directory.CreateDirectory(Path.Combine(m.Rig.Directory, "temp"));
        await File.WriteAllTextAsync(Path.Combine(m.Rig.Directory, "temp", "sobra.tmp"), "x", Token);
        m.Diary.WriteLine("2026-10-21T12:00:00.0000000Z ApplicationStarted");
        await m.Rig.Ui.InvokeAsync(() => m.Player.OpenAsync(m.Rig.Repository.List()[0].Id));

        Assert.NotNull(await vault.ReadAsync(Token));
        Assert.True(File.Exists(Path.Combine(m.Rig.Directory, "settings.json")));
        Assert.True(File.Exists(m.DiaryPath));
        var deleted = 0;
        m.Management.DataDeleted += () => deleted++;

        await RunAsync(m, () => m.Management.DeleteAll);

        Assert.Single(m.Dialogs.DeleteAlls);
        Assert.Contains("chave de acesso", m.Dialogs.DeleteAlls[0]);
        Assert.Contains("Não é possível desfazer", m.Dialogs.DeleteAlls[0]);
        Assert.Equal(["release-all", "after:True"], m.Host.Calls);
        // Diretório do app vazio (sem diário, configurações, temporários nem gravações) e a chave sumiu do cofre de teste.
        Assert.Empty(Directory.EnumerateFileSystemEntries(m.Rig.Directory));
        Assert.Null(await vault.ReadAsync(Token));
        Assert.Equal("Todos os dados do app foram apagados do PC.", m.Management.Message);
        Assert.Equal(1, deleted);
        // O diário foi solto e continua sem recriar o arquivo: a pasta fica vazia.
        m.Diary.WriteLine("depois da limpeza");
        Assert.Empty(Directory.EnumerateFileSystemEntries(m.Rig.Directory));
        // Lista e estado de configuração refletem o novo cenário.
        await TrackingRig.Eventually(() => ListAsync(m).GetAwaiter().GetResult().Count == 0);
        Assert.Equal(Whisper.Recorder.Core.Access.ApiAccessStatus.Absent, m.Rig.Access.Status);
        Assert.Null(m.Rig.Access.BaseAddress);
        Assert.Null(m.Rig.Access.MaskedKey);
        Assert.Null(m.Player.RecordingId);
        Assert.False(m.Rig.Gate.IsBusy);
        // O repositório volta a ser usável.
        Assert.Empty(m.Rig.Repository.List());
    }

    [Fact]
    public async Task DeleteAllKeepsEverythingAndTheDiaryWorkingWhenThereIsARecordingInProgress()
    {
        var vault = new WindowsCredentialVault(target);
        await using var m = await ManagementRig.CreateAsync(vault);
        var id = m.Rig.Seed(RecordingState.Recording, title: "Gravando");
        m.Diary.WriteLine("antes");

        await RunAsync(m, () => m.Management.DeleteAll);

        Assert.Equal("Pare a gravação em andamento antes de gerenciar os dados.", m.Management.Message);
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.NotNull(await vault.ReadAsync(Token));
        Assert.True(File.Exists(Path.Combine(m.Rig.Directory, "settings.json")));
        Assert.Equal(["release-all", "after:False"], m.Host.Calls);
        // O diário foi solto e retomado: continua gravando no mesmo arquivo.
        m.Diary.WriteLine("depois");
        var text = V09DiaryTests.ReadLog(m.DiaryPath);
        Assert.Contains("antes", text);
        Assert.Contains("depois", text);
        Assert.False(m.Rig.Gate.IsBusy);
    }

    [Fact]
    public async Task WhileTheRealRecordingRunsRenameDeleteAndDeleteAllAreOff()
    {
        await using var m = await ManagementRig.CreateAsync();
        var saved = await m.SeedSelectedAsync(RecordingState.Saved, "Salva", seconds: 5);
        await m.Rig.Ui.InvokeAsync(async () =>
        {
            await m.Rig.Screen.RefreshDevicesAsync();
            await m.Rig.Screen.StartAsync();
        });
        Assert.True(await m.Rig.Ui.InvokeAsync(() => m.Rig.Screen.IsRecording));

        await m.Rig.Ui.InvokeAsync(() =>
        {
            Assert.False(m.Management.Rename.CanExecute(null));
            Assert.False(m.Management.Delete.CanExecute(null));
            Assert.False(m.Management.DeleteAll.CanExecute(null));
            Assert.Contains("Pare a gravação", m.Management.Hint);
        });
        // Mesmo chamado direto (atalho de teclado), nada acontece e nenhum diálogo abre.
        await RunAsync(m, () => m.Management.Delete);
        await RunAsync(m, () => m.Management.DeleteAll);
        Assert.Empty(m.Dialogs.Deletes);
        Assert.Empty(m.Dialogs.DeleteAlls);
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(saved)));

        await m.Rig.Ui.InvokeAsync(() => m.Rig.Screen.StopAsync());
        await m.Rig.Ui.InvokeAsync(() =>
        {
            Assert.True(m.Management.Rename.CanExecute(null));
            Assert.True(m.Management.DeleteAll.CanExecute(null));
        });
    }

    [Fact]
    public async Task WhileCleaningUpCaptureUploadAndAccessSettingsAreBlockedAndThenRestored()
    {
        await using var m = await ManagementRig.CreateAsync();
        await m.SeedSelectedAsync(RecordingState.Saved, "Salva", seconds: 5);
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Dialogs.HoldDeleteAll = hold;

        var running = RunAsync(m, () => m.Management.DeleteAll);
        await m.Dialogs.DeleteAllAsked!.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await m.Rig.Ui.InvokeAsync(async () =>
        {
            Assert.True(m.Rig.Gate.IsBusy);
            Assert.True(m.Management.IsBusy);
            Assert.False(m.Rig.Screen.Start.CanExecute(null));
            Assert.False(m.Rig.Flow.CanTranscribe);
            Assert.True(m.Rig.Access.IsLocked);
            Assert.False(m.Management.Delete.CanExecute(null));
            Assert.False(m.Management.DeleteAll.CanExecute(null));
            // O atalho global e a bandeja chamam StartAsync direto: o bloqueio vale também ali.
            await m.Rig.Screen.StartAsync();
            Assert.False(m.Rig.Screen.IsRecording);
            Assert.Contains("Aguarde", m.Rig.Screen.Error);
            // Salvar ou remover a chave não faz nada durante a limpeza.
            await m.Rig.Access.RemoveAsync();
            await m.Rig.Access.SaveAsync("https://outra.example.test", "wsk_nova");
        });
        Assert.Equal(TrackingRig.Key, await m.Rig.Vault.ReadAsync(Token));

        hold.SetResult(false); // o usuário recusa
        await running;

        await m.Rig.Ui.InvokeAsync(() =>
        {
            Assert.False(m.Rig.Gate.IsBusy);
            Assert.True(m.Rig.Screen.Start.CanExecute(null));
            Assert.True(m.Rig.Flow.CanTranscribe);
            Assert.False(m.Rig.Access.IsLocked);
            Assert.Null(m.Rig.Screen.Error);
        });
        Assert.Equal("Nenhum dado foi apagado.", m.Management.Message);
        Assert.Empty(m.Host.Calls);
        Assert.Equal(TrackingRig.Key, await m.Rig.Vault.ReadAsync(Token));
    }
}
