namespace Veyro.Desktop.WifiDirect;

public sealed record WifiDirectPeerConnection(
    string DeviceInformationId,
    string DisplayName,
    string LocalAddress,
    string RemoteAddress);
