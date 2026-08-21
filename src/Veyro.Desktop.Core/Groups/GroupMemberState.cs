using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Groups;

public sealed record GroupMemberState(
    string DeviceId,
    string DisplayName,
    VeyroCapability Capabilities,
    bool CoordinatorEligible,
    bool OnExternalPower,
    byte BatteryPercent,
    uint StabilitySeconds,
    byte MaximumDirectPeers,
    bool IsAvailable,
    long LastSeenAtUnixMilliseconds);
