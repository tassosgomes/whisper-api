using System.Runtime.InteropServices;
using System.Windows;
using Whisper.Recorder.Host.Transcripts;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>Porta da área de transferência do host (V-08): nova tentativa curta e o clipboard real do Windows.</summary>
public sealed class V08ClipboardTests
{
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    [Fact]
    public async Task RetriesBrieflyWhenAnotherProgramHoldsTheClipboardThenSucceeds()
    {
        var calls = new List<string>();
        var waits = new List<TimeSpan>();
        var clipboard = new WindowsClipboard(text =>
        {
            calls.Add(text);
            if (calls.Count < 3)
                throw new COMException("CLIPBRD_E_CANT_OPEN", ClipboardCantOpen);
        }, span =>
        {
            waits.Add(span);
            return Task.CompletedTask;
        });

        Assert.True(await clipboard.TrySetTextAsync("texto"));

        Assert.Equal(3, calls.Count);
        Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.InRange(wait, TimeSpan.Zero, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task GivesUpAfterTheAttemptsAndReportsFailureInsteadOfThrowing()
    {
        var calls = 0;
        var clipboard = new WindowsClipboard(_ =>
        {
            calls++;
            throw new COMException("CLIPBRD_E_CANT_OPEN", ClipboardCantOpen);
        }, _ => Task.CompletedTask, attempts: 3);

        Assert.False(await clipboard.TrySetTextAsync("texto"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task OtherFailuresAreNotHiddenAsClipboardBusy()
    {
        var clipboard = new WindowsClipboard(_ => throw new InvalidOperationException(), _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => clipboard.TrySetTextAsync("texto"));
    }

    /// <summary>
    /// Usa o clipboard real do Windows e devolve o texto anterior ao fim. Se há outro conteúdo (imagem, arquivos),
    /// o teste não o toca. Gerenciadores de histórico da área de transferência podem guardar o texto do teste.
    /// Opcional (WHISPER_TEST_REAL_CLIPBOARD=1): rodar a suíte não pode mexer na área de transferência de quem trabalha.
    /// </summary>
    [Fact]
    public void RealWindowsClipboardReceivesTheTextAndTheOldTextIsRestored()
    {
        if (Environment.GetEnvironmentVariable("WHISPER_TEST_REAL_CLIPBOARD") != "1")
            Assert.Skip("Defina WHISPER_TEST_REAL_CLIPBOARD=1 para usar a área de transferência real.");
        RunOnSta(() =>
        {
            string? previous = null;
            try
            {
                if (Clipboard.ContainsText())
                    previous = Clipboard.GetText();
                else if (Clipboard.GetDataObject()?.GetFormats().Length > 0)
                    return; // conteúdo que não é texto: não sobrescreve a área de transferência do usuário
            }
            catch (COMException)
            {
                return; // outro programa está com a área de transferência: nada a verificar agora
            }

            var text = "Whisper V08 " + Guid.NewGuid().ToString("N") + Environment.NewLine + "[00:00:01.240] Olá";
            try
            {
                Assert.True(new WindowsClipboard().TrySetTextAsync(text).GetAwaiter().GetResult());
                Assert.Equal(text, Clipboard.GetText());
            }
            finally
            {
                try
                {
                    if (previous is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previous, copy: true);
                }
                catch (COMException) { }
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
