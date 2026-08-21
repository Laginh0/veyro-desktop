namespace Veyro.Desktop.Core.Discovery;

public sealed record DiscoveredDevice(
    string EphemeralId,
    ulong BluetoothAddress,
    short SignalStrengthDbm,
    VeyroCapability Capabilities,
    byte ProtocolMajor,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
