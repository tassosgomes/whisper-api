namespace Whisper.Recorder.Core.Storage;

/// <summary>
/// Replaces a file with a fully written temporary one. On Windows the rename is refused while
/// another process (antivirus, indexer) briefly holds the destination open, so it is retried.
/// </summary>
internal static class AtomicFile
{
    private const int Attempts = 10;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    public static void Replace(string temporary, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is UnauthorizedAccessException or IOException
                && File.Exists(temporary))
            {
                Thread.Sleep(Pause);
            }
        }
    }
}
