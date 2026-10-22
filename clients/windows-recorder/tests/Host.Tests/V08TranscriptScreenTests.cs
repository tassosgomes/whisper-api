using Whisper.Recorder.Host.Tests.Doubles;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Tela de transcrição (V-08, host): trechos com o tempo, cópia, exportação com confirmação e transcrição vazia, a partir de
/// arquivos locais. Clipboard e diálogos são dublês; nada toca a rede, o Credential Manager nem a pasta real do app.
/// </summary>
public sealed class V08TranscriptScreenTests
{
    [Fact]
    public async Task OpensLocalFilesWithEachSegmentAndItsTimeAfterPassingThroughLoading()
    {
        using var files = new LocalTranscriptFiles();
        var screen = files.Screen(new FakeClipboard(), new FakeDialogs());
        var states = new List<bool>();
        screen.PropertyChanged += (_, _) => states.Add(screen.IsLoading);

        await screen.OpenAsync(files.Id);

        Assert.Equal(new[] { "00:00:01.240", "01:01:01.007" }, screen.Lines.Select(l => l.Time));
        Assert.Equal(new[] { "Olá reunião", "Outro trecho" }, screen.Lines.Select(l => l.Text));
        Assert.Equal("00:00:01.240: Olá reunião", screen.Lines[0].AutomationName);
        Assert.Equal("Reunião: Planejamento/Q4", screen.Title);
        Assert.Contains("duração 01:02:00", screen.Details);
        Assert.True(screen.ShowsLines);
        Assert.False(screen.IsEmpty);
        Assert.Equal("Transcrição guardada no PC.", screen.Status);
        Assert.Contains(true, states);
        Assert.False(screen.IsLoading);
    }

    [Fact]
    public async Task CopyWithAndWithoutTimesSendsEachTextToTheClipboard()
    {
        using var files = new LocalTranscriptFiles();
        var clipboard = new FakeClipboard();
        var screen = files.Screen(clipboard, new FakeDialogs());
        await screen.OpenAsync(files.Id);

        await screen.CopyWithTimes.ExecuteAsync();
        Assert.Equal("Transcrição copiada com os tempos.", screen.Status);
        await screen.CopyWithoutTimes.ExecuteAsync();
        Assert.Equal("Transcrição copiada sem os tempos.", screen.Status);

        Assert.Equal(new[] { LocalTranscriptFiles.WithTimes, LocalTranscriptFiles.WithoutTimes }, clipboard.Copied);
    }

    [Fact]
    public async Task ClipboardFailureIsReportedInPlainLanguageAndNothingIsLost()
    {
        using var files = new LocalTranscriptFiles();
        var clipboard = new FakeClipboard { Succeeds = false };
        var screen = files.Screen(clipboard, new FakeDialogs());
        await screen.OpenAsync(files.Id);

        await screen.CopyWithTimes.ExecuteAsync();

        Assert.Empty(clipboard.Copied);
        Assert.StartsWith("Não foi possível copiar", screen.Status);
        Assert.True(screen.CanCopy);
        Assert.Equal(2, screen.Lines.Count);
    }

    [Fact]
    public async Task ExportsToTheChosenNewPathWithoutAskingToOverwrite()
    {
        using var files = new LocalTranscriptFiles();
        var dialogs = new FakeDialogs { Path = Path.Combine(files.Folder("saida"), "nova.txt") };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        await screen.Export.ExecuteAsync();

        Assert.Equal(LocalTranscriptFiles.WithTimes, File.ReadAllText(dialogs.Path!));
        Assert.Empty(dialogs.Confirmations);
        Assert.Equal("Transcrição exportada para nova.txt.", screen.Status);
        // O nome sugerido não leva caracteres inválidos do título.
        Assert.Equal("Reunião Planejamento Q4 - transcrição.txt", Assert.Single(dialogs.SuggestedNames));
    }

    [Fact]
    public async Task ExistingFileIsOnlyReplacedAfterTheUserConfirms()
    {
        using var files = new LocalTranscriptFiles();
        var path = Path.Combine(files.Folder("saida"), "repetido.txt");
        File.WriteAllText(path, "conteúdo antigo");
        var dialogs = new FakeDialogs { Path = path, Overwrite = false };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        await screen.Export.ExecuteAsync();
        Assert.Equal(new[] { path }, dialogs.Confirmations);
        Assert.Equal("conteúdo antigo", File.ReadAllText(path));
        Assert.Equal("Exportação cancelada. O arquivo existente foi mantido.", screen.Status);

        dialogs.Overwrite = true;
        await screen.Export.ExecuteAsync();
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.Equal(LocalTranscriptFiles.WithTimes, File.ReadAllText(path));
        Assert.Equal("Transcrição exportada para repetido.txt.", screen.Status);
    }

    [Fact]
    public async Task CancellingTheFileChooserWritesNothing()
    {
        using var files = new LocalTranscriptFiles();
        var dialogs = new FakeDialogs { Path = null };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        await screen.Export.ExecuteAsync();

        Assert.Empty(dialogs.Confirmations);
        Assert.Equal("Exportação cancelada.", screen.Status);
    }

    [Fact]
    public async Task ExportToAnUnavailableFolderShowsAnErrorAndKeepsTheTranscript()
    {
        using var files = new LocalTranscriptFiles();
        var dialogs = new FakeDialogs { Path = Path.Combine(files.Root, "nao-existe", "x.txt") };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        await screen.Export.ExecuteAsync();

        Assert.StartsWith("Não foi possível exportar", screen.Status);
        Assert.Empty(dialogs.Confirmations);
        Assert.True(screen.CanExport);
        Assert.Equal(2, screen.Lines.Count);
    }

    [Fact]
    public async Task EmptyTranscriptShowsItsOwnMessageAndDisablesCopyAndExport()
    {
        using var files = new LocalTranscriptFiles(segmentsJson: "[]", text: "");
        var clipboard = new FakeClipboard();
        var dialogs = new FakeDialogs { Path = Path.Combine(files.Folder("saida"), "vazia.txt") };
        var screen = files.Screen(clipboard, dialogs);

        await screen.OpenAsync(files.Id);

        Assert.True(screen.IsEmpty);
        Assert.False(screen.ShowsLines);
        Assert.Equal("Nenhuma fala foi reconhecida.", screen.Status);
        Assert.False(screen.CanCopy);
        Assert.False(screen.CopyWithTimes.CanExecute(null));
        Assert.False(screen.CopyWithoutTimes.CanExecute(null));
        Assert.False(screen.Export.CanExecute(null));
        await screen.Export.ExecuteAsync();
        Assert.Empty(clipboard.Copied);
        Assert.False(File.Exists(dialogs.Path));
    }

    [Fact]
    public async Task UnreadableFilesShowAFixedMessageWithoutLocalPaths()
    {
        using var files = new LocalTranscriptFiles();
        File.WriteAllText(Path.Combine(files.Repository.DirectoryFor(files.Id), "result-v1.json"), "{ não é json");
        var screen = files.Screen(new FakeClipboard(), new FakeDialogs());

        await screen.OpenAsync(files.Id);

        Assert.Equal("Não foi possível ler a transcrição local. Confira os arquivos no PC.", screen.Status);
        Assert.False(screen.CanCopy);
        Assert.False(screen.IsLoading);
        Assert.Empty(screen.Lines);
    }

    [Fact]
    public async Task FileCreatedWhileTheChooserIsOpenIsNeverReplacedWithoutConfirmation()
    {
        using var files = new LocalTranscriptFiles();
        var path = Path.Combine(files.Folder("saida"), "aparece.txt");
        var dialogs = new FakeDialogs { Path = path, Overwrite = false, BeforeReturning = p => File.WriteAllText(p, "conteúdo prévio") };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        await screen.Export.ExecuteAsync();

        Assert.Equal(new[] { path }, dialogs.Confirmations);
        Assert.Equal("conteúdo prévio", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task FailedOverwriteKeepsTheOriginalFileAndReportsAFixedError()
    {
        using var files = new LocalTranscriptFiles();
        var path = Path.Combine(files.Folder("saida"), "aberto.txt");
        File.WriteAllText(path, "original");
        var dialogs = new FakeDialogs { Path = path, Overwrite = true };
        var screen = files.Screen(new FakeClipboard(), dialogs);
        await screen.OpenAsync(files.Id);

        // Outro programa segura o arquivo aberto: a troca falha e o original não é truncado.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await screen.Export.ExecuteAsync();

        Assert.Equal("original", File.ReadAllText(path));
        Assert.StartsWith("Não foi possível exportar", screen.Status);
        Assert.DoesNotContain(files.Root, screen.Status);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.True(screen.CanExport);
    }

    [Fact]
    public async Task TwoOpensInARowEndWithTheSecondRecordingAndStopLoadingOnlyAtTheEnd()
    {
        using var files = new LocalTranscriptFiles(segmentsJson: "[" + string.Join(",", Enumerable.Range(0, 40000)
            .Select(i => "{\"startMs\":" + i + ",\"text\":\"primeira " + i + "\"}")) + "]");
        var second = files.AddTranscribed("Segunda", "[{\"startMs\":7000,\"text\":\"da segunda\"}]");
        var screen = files.Screen(new FakeClipboard(), new FakeDialogs());
        var loadingStates = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var secondStarted = false;
        screen.PropertyChanged += (_, _) =>
        {
            if (Volatile.Read(ref secondStarted)) loadingStates.Enqueue(screen.IsLoading);
        };

        var firstOpen = screen.OpenAsync(files.Id);
        var secondOpen = screen.OpenAsync(second);
        Volatile.Write(ref secondStarted, true);
        await Task.WhenAll(firstOpen, secondOpen);

        Assert.Equal("Segunda", screen.Title);
        Assert.Equal("da segunda", Assert.Single(screen.Lines).Text);
        Assert.True(screen.ShowsLines);
        Assert.False(screen.IsLoading);
        // Depois que a segunda começou, só o fim da segunda leitura tira a tela de "carregando".
        Assert.Equal(1, loadingStates.Count(loading => !loading));
    }
}
