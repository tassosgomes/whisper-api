using System.ComponentModel;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;
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
    private readonly RecordingManagementScreenViewModel management;
    private bool showDataResult;
    private bool busy;

    public ApiAccessWindow(ApiAccessViewModel access, RecordingManagementScreenViewModel management)
    {
        this.access = access;
        this.management = management;
        InitializeComponent();
        DataContext = access;
        access.PropertyChanged += OnAccessChanged;
        management.PropertyChanged += OnAccessChanged;
        management.DataDeleted += OnDataDeleted;
        DataLocationText.Text = management.DataLocationText;
        Closed += (_, _) =>
        {
            access.PropertyChanged -= OnAccessChanged;
            management.PropertyChanged -= OnAccessChanged;
            management.DataDeleted -= OnDataDeleted;
        };
        Loaded += async (_, _) =>
        {
            Refresh();
            await RunAsync(() => access.InitializeAsync());
            AddressBox.Text = access.BaseAddress?.AbsoluteUri ?? AddressBox.Text;
            Refresh();
        };
    }

    private void OnAccessChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>Tudo foi apagado: endereço e chave digitados não podem sobreviver à limpeza.</summary>
    private void OnDataDeleted()
    {
        AddressBox.Clear();
        KeyBox.Clear();
    }

    private async void OnDeleteAll(object sender, System.Windows.RoutedEventArgs e)
    {
        // A confirmação e a ordem das etapas são do ViewModel; aqui só se mostra o resultado.
        await management.DeleteAll.ExecuteAsync();
        showDataResult = true;
        Refresh();
    }

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
        // Durante a limpeza dos dados a chave não pode ser salva nem removida por aqui.
        SaveButton.IsEnabled = !busy && !access.IsLocked;
        RemoveButton.IsEnabled = !busy && !access.IsLocked && access.MaskedKey is not null;
        DeleteAllButton.IsEnabled = management.CanDeleteAll;
        DataStatusText.Text = management.IsBusy ? "Apagando os dados do app…"
            : !management.CanDeleteAll ? management.Hint
            : showDataResult ? management.Message : "";
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
