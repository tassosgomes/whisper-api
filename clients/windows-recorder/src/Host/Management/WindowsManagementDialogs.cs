using System.Windows;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Management;

/// <summary>
/// Diálogos da gestão de dados. As confirmações de apagar usam a caixa de mensagem do Windows com "Não" como resposta
/// padrão (Enter ou Esc nunca apagam), ícone de aviso e texto completo lido por leitores de tela. O texto do aviso vem do núcleo.
/// </summary>
public sealed class WindowsManagementDialogs(Func<Window?> owner) : IManagementDialogs
{
    public Task<string?> PromptTitleAsync(string currentTitle)
    {
        var window = new RenameWindow(currentTitle);
        if (owner() is { } parent && parent.IsVisible)
            window.Owner = parent;
        return Task.FromResult(window.ShowDialog() == true ? window.NewTitle : null);
    }

    public Task<bool> ConfirmDeleteAsync(DeleteRecordingConfirmation confirmation) =>
        Task.FromResult(Ask("Apagar a gravação?", $"\"{confirmation.Title}\"\n\n{Plain(confirmation)}\n\nDeseja apagar?"));

    public Task<bool> ConfirmPendingDeleteAsync(DeleteRecordingConfirmation confirmation) =>
        Task.FromResult(Ask("Envio ou transcrição em andamento",
            $"\"{confirmation.Title}\"\n\n{confirmation.Message}\n\nApagar mesmo assim?"));

    public Task<bool> ConfirmDeleteAllAsync(string message) =>
        Task.FromResult(Ask("Apagar todos os dados do app?", message + "\n\nDeseja apagar tudo?"));

    // Na confirmação simples, só o texto geral: o aviso de envio em andamento tem o seu próprio diálogo.
    private static string Plain(DeleteRecordingConfirmation confirmation) =>
        confirmation.HasPendingTranscription
            ? "Apagar remove o áudio, a transcrição e os metadados do PC. Não é possível desfazer."
            : confirmation.Message;

    private bool Ask(string title, string text)
    {
        var parent = owner();
        var result = parent is { IsVisible: true }
            ? MessageBox.Show(parent, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
