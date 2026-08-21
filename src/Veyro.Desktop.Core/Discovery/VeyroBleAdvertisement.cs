namespace Veyro.Desktop.Core.Discovery;

public sealed record VeyroBleAdvertisement(
    byte ProtocolMajor,
    VeyroCapability Capabilities,
    string EphemeralId);
