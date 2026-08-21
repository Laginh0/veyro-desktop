namespace Veyro.Desktop.Core.Groups;

public sealed class GroupStateManager
{
    private readonly object sync = new();
    private readonly Dictionary<string, GroupMemberState> members = new(StringComparer.Ordinal);

    public GroupStateManager(GroupMemberState localMember)
    {
        ArgumentNullException.ThrowIfNull(localMember);
        members[localMember.DeviceId] = localMember with { IsAvailable = true };
        LocalDeviceId = localMember.DeviceId;
        CoordinatorDeviceId = localMember.DeviceId;
        Epoch = 1;
    }

    public event EventHandler<GroupStateChangedEventArgs>? StateChanged;

    public string LocalDeviceId { get; }

    public ulong Epoch { get; private set; }

    public string CoordinatorDeviceId { get; private set; }

    public bool LocalIsCoordinator => string.Equals(
        LocalDeviceId,
        CoordinatorDeviceId,
        StringComparison.Ordinal);

    public IReadOnlyList<GroupMemberState> Snapshot()
    {
        lock (sync)
        {
            return members.Values.OrderBy(member => member.DeviceId, StringComparer.Ordinal).ToArray();
        }
    }

    public void Upsert(GroupMemberState member, string reason = "member_updated")
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (sync)
        {
            members[member.DeviceId] = member;
            if (!members.TryGetValue(CoordinatorDeviceId, out var coordinator) || !coordinator.IsAvailable)
            {
                ElectLocked("coordinator_unavailable");
            }
            else
            {
                RaiseChangedLocked(reason);
            }
        }
    }

    public void MarkUnavailable(string deviceId, long lastSeenAtUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            if (!members.TryGetValue(deviceId, out var member))
            {
                return;
            }

            members[deviceId] = member with
            {
                IsAvailable = false,
                LastSeenAtUnixMilliseconds = lastSeenAtUnixMilliseconds
            };
            if (string.Equals(deviceId, CoordinatorDeviceId, StringComparison.Ordinal))
            {
                ElectLocked("coordinator_disconnected");
            }
            else
            {
                RaiseChangedLocked("member_disconnected");
            }
        }
    }

    public string Elect(string reason)
    {
        lock (sync)
        {
            return ElectLocked(reason);
        }
    }

    public void AdoptInitialCoordinator(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            if (Epoch != 1 ||
                !members.TryGetValue(deviceId, out var member) ||
                !member.IsAvailable ||
                !member.CoordinatorEligible)
            {
                throw new InvalidOperationException("The initial coordinator is not an eligible group member.");
            }

            CoordinatorDeviceId = deviceId;
            RaiseChangedLocked("wifi_direct_group_owner");
        }
    }

    public bool Apply(GroupControlMessage control)
    {
        ArgumentNullException.ThrowIfNull(control);
        lock (sync)
        {
            var selectedCoordinator = CoordinatorElection.Select(
                control.Members,
                control.CoordinatorDeviceId);
            if (control.Epoch < Epoch ||
                (control.Epoch == Epoch &&
                    !string.Equals(
                        control.CoordinatorDeviceId,
                        CoordinatorDeviceId,
                        StringComparison.Ordinal)) ||
                selectedCoordinator is null ||
                !string.Equals(
                    selectedCoordinator.DeviceId,
                    control.CoordinatorDeviceId,
                    StringComparison.Ordinal) ||
                !control.Members.Any(member =>
                    member.IsAvailable &&
                    string.Equals(member.DeviceId, LocalDeviceId, StringComparison.Ordinal)) ||
                !control.Members.Any(member =>
                    member.IsAvailable &&
                    string.Equals(member.DeviceId, control.CoordinatorDeviceId, StringComparison.Ordinal)))
            {
                return false;
            }

            members.Clear();
            foreach (var member in control.Members)
            {
                members[member.DeviceId] = member;
            }

            Epoch = control.Epoch;
            CoordinatorDeviceId = control.CoordinatorDeviceId;
            RaiseChangedLocked(control.Reason);
            return true;
        }
    }

    public GroupControlMessage CreateControlMessage(GroupControlKind kind, string reason)
    {
        lock (sync)
        {
            return new GroupControlMessage(
                GroupControlCodec.CurrentVersion,
                kind,
                Epoch,
                CoordinatorDeviceId,
                LocalDeviceId,
                reason,
                members.Values.OrderBy(member => member.DeviceId, StringComparer.Ordinal).ToArray());
        }
    }

    private string ElectLocked(string reason)
    {
        var selected = CoordinatorElection.Select(members.Values, CoordinatorDeviceId)
            ?? throw new InvalidOperationException("No eligible Veyro coordinator is available.");
        if (!string.Equals(selected.DeviceId, CoordinatorDeviceId, StringComparison.Ordinal))
        {
            CoordinatorDeviceId = selected.DeviceId;
            Epoch++;
        }

        RaiseChangedLocked(reason);
        return CoordinatorDeviceId;
    }

    private void RaiseChangedLocked(string reason) =>
        StateChanged?.Invoke(
            this,
            new GroupStateChangedEventArgs(
                Epoch,
                CoordinatorDeviceId,
                members.Values.OrderBy(member => member.DeviceId, StringComparer.Ordinal).ToArray(),
                reason));
}
