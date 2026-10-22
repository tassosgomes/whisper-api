using System.IO;
using System.Text;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Escritor do diário (<c>diagnostics.log</c>) que pode soltar o arquivo. O app o mantém aberto; "apagar todos os dados"
/// precisa liberá-lo antes de esvaziar a pasta. Abre sob demanda (acrescentando, com leitura compartilhada) e escreve cada
/// linha direto no arquivo, sem buffer. Falha de disco nunca chega ao chamador: o diário é opcional.
/// Depois de <see cref="Release"/> as linhas são descartadas até <see cref="Resume"/>: assim a pasta apagada não ganha
/// um arquivo novo no meio da limpeza, nem na saída do app.
/// </summary>
public sealed class ReleasableLogWriter(string path) : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock gate = new();
    private FileStream? stream;
    private bool released;
    private bool disposed;

    public override Encoding Encoding => Utf8;

    /// <summary>Fecha o arquivo e descarta o que for escrito até <see cref="Resume"/>.</summary>
    public void Release()
    {
        lock (gate)
        {
            released = true;
            Close(ref stream);
        }
    }

    /// <summary>Volta a escrever (o arquivo é reaberto na próxima linha). Usado quando a limpeza não aconteceu.</summary>
    public void Resume()
    {
        lock (gate)
            released = false;
    }

    public override void Write(char value) => Append(value.ToString());

    public override void Write(string? value) => Append(value);

    public override void WriteLine(string? value) => Append(value + Environment.NewLine);

    public override void Flush()
    {
        // Cada linha já vai direto ao arquivo.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                disposed = true;
                Close(ref stream);
            }
        }
        base.Dispose(disposing);
    }

    private void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (gate)
        {
            if (released || disposed)
                return;
            try
            {
                if (stream is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
                var bytes = Utf8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Não registra a exceção: a mensagem traz caminhos locais. A próxima linha tenta de novo.
                Close(ref stream);
            }
        }
    }

    private static void Close(ref FileStream? file)
    {
        try { file?.Dispose(); }
        catch (IOException) { }
        file = null;
    }
}
