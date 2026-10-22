using System.Net.NetworkInformation;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Conectividade do Windows guardada em memória: o acompanhamento consulta a cada 2 segundos e não deve
/// enumerar adaptadores de rede a cada consulta. Os eventos do sistema mantêm o valor atual.
/// </summary>
public sealed class WindowsConnectivity : IConnectivity, IDisposable
{
    private volatile bool connected = NetworkInterface.GetIsNetworkAvailable();

    public WindowsConnectivity()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public bool IsConnected => connected;

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => connected = e.IsAvailable;

    // Troca de rede (Wi-Fi para cabo, VPN) nem sempre muda a disponibilidade: confere de novo.
    private void OnAddressChanged(object? sender, EventArgs e) => connected = NetworkInterface.GetIsNetworkAvailable();

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }
}
