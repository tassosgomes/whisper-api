using System.Text;
using Whisper.Recorder.Host.Shell;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Diário do app (V-09, host): o arquivo fica aberto durante o uso, é solto para "apagar todos os dados" e não é recriado
/// enquanto estiver solto. Usa uma pasta temporária; nunca o diário real do app.
/// </summary>
public sealed class V09DiaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "whisper-host-v09-diary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Lê com o diário ainda aberto pelo app (compartilhamento de leitura e escrita, como um editor de texto).</summary>
    internal static string ReadLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheOpenDiaryBlocksDeletionUntilReleasedAndIsNotRecreatedWhileReleased()
    {
        var path = Path.Combine(root, "diagnostics.log");
        using var diary = new ReleasableLogWriter(path);
        diary.WriteLine("primeira");
        diary.WriteLine("segunda");
        Assert.Equal("primeira" + Environment.NewLine + "segunda" + Environment.NewLine, ReadLog(path));

        // Aberto pelo app, o arquivo não pode ser apagado: é por isso que a limpeza precisa soltá-lo.
        Assert.Throws<IOException>(() => File.Delete(path));

        diary.Release();
        File.Delete(path);
        diary.WriteLine("descartada");
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void ResumingReopensTheSameFileAppendingAndAnUnwritableFileNeverThrows()
    {
        var path = Path.Combine(root, "diagnostics.log");
        using var diary = new ReleasableLogWriter(path);
        diary.WriteLine("antes");
        diary.Release();
        diary.Resume();
        diary.WriteLine("depois");
        diary.Flush();
        Assert.Equal("antes" + Environment.NewLine + "depois" + Environment.NewLine, ReadLog(path));

        // Outro processo segura o arquivo sem compartilhar: o diário é opcional e a falha não chega ao chamador.
        diary.Release();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            diary.Resume();
            diary.WriteLine("não consegue gravar");
        }
        diary.WriteLine("volta a gravar");
        Assert.EndsWith("volta a gravar" + Environment.NewLine, ReadLog(path), StringComparison.Ordinal);
    }
}
