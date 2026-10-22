using System.Runtime.InteropServices;
using System.Windows;
using Whisper.Recorder.Presentation.Transcripts;

namespace Whisper.Recorder.Host.Transcripts;

/// <summary>
/// Área de transferência do Windows. Outro programa pode estar com ela aberta (CLIPBRD_E_CANT_OPEN): tenta poucas vezes,
/// com espera curta e sem bloquear a interface, e só então informa a falha. Chamar na thread da interface (STA).
/// </summary>
public sealed class WindowsClipboard(Action<string>? setText = null, Func<TimeSpan, Task>? delay = null,
    int attempts = 3, TimeSpan? interval = null) : ITranscriptClipboard
{
    private readonly Action<string> set = setText ?? (text => Clipboard.SetDataObject(text, copy: true));
    private readonly Func<TimeSpan, Task> wait = delay ?? (span => Task.Delay(span));
    private readonly TimeSpan pause = interval ?? TimeSpan.FromMilliseconds(100);

    public async Task<bool> TrySetTextAsync(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                set(text);
                return true;
            }
            catch (COMException) when (attempt >= attempts)
            {
                return false;
            }
            catch (COMException)
            {
                await wait(pause);
            }
        }
    }
}
