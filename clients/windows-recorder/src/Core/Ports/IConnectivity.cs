using System.Net.NetworkInformation;

namespace Whisper.Recorder.Core.Ports;

public interface IConnectivity
{
    bool IsConnected { get; }
}

public sealed class SystemConnectivity : IConnectivity
{
    public bool IsConnected => NetworkInterface.GetIsNetworkAvailable();
}
