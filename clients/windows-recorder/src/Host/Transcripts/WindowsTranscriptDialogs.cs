using System.IO;
using System.Windows;
using Microsoft.Win32;
using Whisper.Recorder.Presentation.Transcripts;

namespace Whisper.Recorder.Host.Transcripts;

/// <summary>Seletor de arquivo do sistema e confirmação de sobrescrita. O aviso nativo de sobrescrita fica desligado: a confirmação é única e vem da regra do núcleo.</summary>
public sealed class WindowsTranscriptDialogs(Func<Window?> owner) : ITranscriptDialogs
{
    public string? ChooseExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar a transcrição",
            Filter = "Texto (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = suggestedFileName,
            OverwritePrompt = false,
            CheckPathExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return (owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    public async Task<bool> ConfirmOverwriteAsync(string path)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Substituir o arquivo?",
            Content = $"O arquivo \"{Path.GetFileName(path)}\" já existe. Deseja substituí-lo pela transcrição?",
            PrimaryButtonText = "Substituir",
            CloseButtonText = "Cancelar",
            Owner = owner()
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
