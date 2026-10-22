using System.Diagnostics;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Uploading;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Segurança dos dados (V-09, host): apagar uma gravação não cancela o envio de outra, a trava sempre é liberada e
/// "apagar tudo" não segue junções para fora da pasta do app. Pasta temporária, cofre em memória e API em memória.
/// </summary>
public sealed class V09DataSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task RunAsync(ManagementRig m, Func<AsyncRelayCommand> command) =>
        m.Rig.Ui.InvokeAsync(() => command().ExecuteAsync());

    [Fact]
    public async Task DeletingAnotherSelectedRecordingDoesNotCancelTheUploadInProgress()
    {
        var service = new GatedUploadService();
        await using var m = await ManagementRig.CreateAsync(uploadHandler: service);
        var sending = await m.SeedSelectedAsync(RecordingState.Saved, "Em envio", seconds: 5);
        await m.Rig.Ui.InvokeAsync(() => m.Rig.Flow.TranscribeAsync());
        Assert.Equal(UploadPhase.Confirming, m.Rig.Flow.Phase);
        var upload = m.Rig.Ui.InvokeAsync(() => m.Rig.Flow.ConfirmAsync());
        await service.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(UploadPhase.Sending, m.Rig.Flow.Phase);

        // Outra gravação passa a ser a selecionada e é apagada: o envio da primeira não é dela para cancelar.
        var other = await m.SeedSelectedAsync(RecordingState.Saved, "Outra", seconds: 5);
        Assert.Equal(other, m.Rig.Flow.SelectedRecordingId);
        var deleting = RunAsync(m, () => m.Management.Delete);
        await TrackingRig.Eventually(() => m.Host.Calls.Contains("release:" + other));
        service.Release.SetResult();
        await upload;
        await deleting;

        Assert.Equal(UploadPhase.Accepted, m.Rig.Flow.Phase);
        Assert.Equal(RecordingState.Processing, m.Rig.Repository.Read(sending).State);
        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(other)));
        Assert.True(Directory.Exists(m.Rig.Repository.DirectoryFor(sending)));
    }

    [Fact]
    public async Task DeletingTheRecordingBeingSentCancelsItsOwnUpload()
    {
        var service = new GatedUploadService();
        await using var m = await ManagementRig.CreateAsync(uploadHandler: service);
        var id = await m.SeedSelectedAsync(RecordingState.Saved, "Em envio", seconds: 5);
        await m.Rig.Ui.InvokeAsync(() => m.Rig.Flow.TranscribeAsync());
        var upload = m.Rig.Ui.InvokeAsync(() => m.Rig.Flow.ConfirmAsync());
        await service.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var deleting = RunAsync(m, () => m.Management.Delete);
        await TrackingRig.Eventually(() => m.Host.Calls.Contains("release:" + id));
        await upload.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await deleting.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(Directory.Exists(m.Rig.Repository.DirectoryFor(id)));
        Assert.Null(m.Rig.Flow.SelectedRecordingId);
    }

    [Fact]
    public async Task TheGateIsReleasedEvenWhenTheHostFailsAfterDeleteAll()
    {
        await using var m = await ManagementRig.CreateAsync();
        m.Rig.Seed(RecordingState.Saved, title: "Uma");
        var failing = await m.Rig.Ui.InvokeAsync(() => new RecordingManagementScreenViewModel(
            new RecordingManagementViewModel(new RecordingManagement(m.Rig.Repository, m.Rig.Vault, m.Rig.Directory)),
            m.Rig.Screen, m.Rig.Flow, m.Rig.Access, m.Dialogs, new FailingAfterHost(m.Host), m.Rig.Gate, m.Rig.Directory));

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Rig.Ui.InvokeAsync(() => failing.DeleteAll.ExecuteAsync()));

        Assert.False(m.Rig.Gate.IsBusy);
        await m.Rig.Ui.InvokeAsync(() =>
        {
            Assert.True(m.Rig.Screen.Start.CanExecute(null));
            Assert.False(m.Rig.Access.IsLocked);
            Assert.True(failing.DeleteAll.CanExecute(null));
        });
        failing.Dispose();
    }

    private sealed class FailingAfterHost(TestManagementHost inner) : IManagementHost
    {
        public Task ReleaseRecordingAsync(Guid id) => inner.ReleaseRecordingAsync(id);
        public Task ReleaseAllAsync() => inner.ReleaseAllAsync();
        public void AfterDeleteAll(bool completed) => throw new InvalidOperationException("falha simulada do host");
    }

    [Fact]
    public async Task DeleteAllRemovesJunctionsWithoutFollowingThemIntoTheOutsideFolder()
    {
        var outside = Path.Combine(Path.GetTempPath(), "whisper-host-v09-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        var keepA = Path.Combine(outside, "importante.txt");
        var keepB = Path.Combine(outside, "sub", "outro.txt");
        await File.WriteAllTextAsync(keepA, "não apagar", Token);
        await File.WriteAllTextAsync(keepB, "não apagar também", Token);
        try
        {
            await using var m = await ManagementRig.CreateAsync();
            var id = m.Rig.Seed(RecordingState.Saved, title: "Com junção");
            // Uma junção na raiz do app e outra dentro da pasta de uma gravação, ambas apontando para fora do app.
            if (!TryLinkDirectory(Path.Combine(m.Rig.Directory, "atalho"), outside)
                || !TryLinkDirectory(Path.Combine(m.Rig.Repository.DirectoryFor(id), "atalho"), outside))
            {
                Assert.Skip("Não foi possível criar junção nem link simbólico de diretório neste ambiente.");
                return;
            }

            await m.SelectAsync(id);
            await RunAsync(m, () => m.Management.DeleteAll);

            Assert.Equal("Todos os dados do app foram apagados do PC.", m.Management.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(m.Rig.Directory));
            // O destino externo e o seu conteúdo continuam intactos: o link foi removido, não seguido.
            Assert.Equal("não apagar", await File.ReadAllTextAsync(keepA, Token));
            Assert.Equal("não apagar também", await File.ReadAllTextAsync(keepB, Token));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var process = Process.Start(info)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0 && Directory.Exists(link))
                return true;
        }
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
