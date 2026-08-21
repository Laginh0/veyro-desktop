namespace Veyro.Desktop.Core.Pairing;

public sealed record PairingVerification(
    string Pin,
    string RemoteDeviceId,
    string RemoteDisplayName);
