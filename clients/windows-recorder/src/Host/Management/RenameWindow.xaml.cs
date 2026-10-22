using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host.Management;

/// <summary>
/// Pede o novo título. Enter salva, Esc cancela; vazio é um título válido (volta ao padrão de data e hora).
/// Só coleta o texto: quem renomeia é o núcleo.
/// </summary>
public partial class RenameWindow : FluentWindow
{
    public RenameWindow(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>O texto digitado; só vale quando a janela fechou com "Salvar".</summary>
    public string NewTitle => TitleBox.Text;

    private void OnSave(object sender, System.Windows.RoutedEventArgs e) => DialogResult = true;
}
