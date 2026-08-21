using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Groups;

public static class CoordinatorElection
{
    public static GroupMemberState? Select(
        IEnumerable<GroupMemberState> members,
        string? currentCoordinatorDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members
            .Where(member =>
                member.IsAvailable &&
                member.CoordinatorEligible &&
                member.Capabilities.HasFlag(VeyroCapability.WifiDirectData) &&
                member.Capabilities.HasFlag(VeyroCapability.MultiDeviceRouting))
            .OrderByDescending(member => Score(member, currentCoordinatorDeviceId))
            .ThenBy(member => member.DeviceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static long Score(GroupMemberState member, string? currentCoordinatorDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        var score = 0L;
        if (member.OnExternalPower)
        {
            score += 1_000_000;
        }

        if (string.Equals(member.DeviceId, currentCoordinatorDeviceId, StringComparison.Ordinal))
        {
            score += 100_000;
        }

        score += member.MaximumDirectPeers * 10_000L;
        score += member.BatteryPercent * 100L;
        score += Math.Min(member.StabilitySeconds, 86_400);
        return score;
    }
}
