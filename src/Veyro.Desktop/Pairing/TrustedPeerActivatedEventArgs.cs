namespace Veyro.Desktop.Pairing;

public sealed class TrustedPeerActivatedEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}
