using System.ComponentModel;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Presentation.Access;
using Wpf.Ui.Controls;

namespace Whisper.Recorder.Host.Access;

/// <summary>
/// Tela de configurações do acesso à API. Só coleta os campos e delega ao <see cref="ApiAccessViewModel"/>;
/// a regra, a validação e o cofre são do núcleo. A API Key nunca é ligada por binding: é lida do campo
/// de senha no clique, o campo é limpo antes de qualquer espera e a chave não é guardada em nenhum campo.
/// </summary>
public partial class ApiAccessWindow : FluentWindow
{
    private readonly ApiAccessViewModel access;
    private bool busy;

    public ApiAccessWindow(ApiAccessViewModel access)
    {
        this.access = access;
        InitializeComponent();
        DataContext = access;
        access.PropertyChanged += OnAccessChanged;
        Closed += (_, _) => access.PropertyChanged -= OnAccessChanged;
        Loaded += async (_, _) =>
        {
            Refresh();
            await RunAsync(() => access.InitializeAsync());
            AddressBox.Text = access.BaseAddress?.AbsoluteUri ?? AddressBox.Text;
            Refresh();
        };
    }

    private void OnAccessChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private async void OnSave(object sender, System.Windows.RoutedEventArgs e)
    {
        var address = AddressBox.Text;
        // A chave sai do campo antes de qualquer await e não é mantida além desta chamada.
        var key = KeyBox.Password;
        KeyBox.Clear();
        await RunAsync(() => access.SaveAsync(address, key));
        if (access.Status == ApiAccessStatus.Confirmed)
            AddressBox.Text = access.BaseAddress?.AbsoluteUri ?? address;
    }

    private async void OnRemove(object sender, System.Windows.RoutedEventArgs e)
    {
        KeyBox.Clear();
        await RunAsync(() => access.RemoveAsync());
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        Refresh();
        try { await action(); }
        finally
        {
            busy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        SaveButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy && access.MaskedKey is not null;
        MaskedKeyText.Text = access.MaskedKey is { } masked ? "Chave salva: " + masked : "Nenhuma chave salva.";
        StatusBar.Message = busy ? "Verificando o acesso…" : access.Message;
        StatusBar.Severity = busy ? InfoBarSeverity.Informational : access.Status switch
        {
            ApiAccessStatus.Confirmed => InfoBarSeverity.Success,
            ApiAccessStatus.Rejected => InfoBarSeverity.Error,
            ApiAccessStatus.Unverifiable => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
    }
}
