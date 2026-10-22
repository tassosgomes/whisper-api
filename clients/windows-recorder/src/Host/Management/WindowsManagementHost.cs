using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Management;

/// <summary>
/// Libera o que o host mantém aberto antes de o núcleo apagar arquivos: as janelas de transcrição e do player da gravação
/// (ou de todas) e o diário. Depois de apagar tudo o diário não volta a ser criado até o app ser reiniciado: assim a pasta
/// do app fica vazia, inclusive na saída. Se a limpeza falhar, o diário volta a ser usado.
/// </summary>
public sealed class WindowsManagementHost(Func<MainWindow?> window, ReleasableLogWriter diary) : IManagementHost
{
    public Task ReleaseRecordingAsync(Guid id) => window()?.ReleaseRecordingAsync(id) ?? Task.CompletedTask;

    public async Task ReleaseAllAsync()
    {
        if (window() is { } main)
            await main.ReleaseAllAsync();
        diary.Release();
    }

    public void AfterDeleteAll(bool completed)
    {
        if (!completed)
            diary.Resume();
    }
}
