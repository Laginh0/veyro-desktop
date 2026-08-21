namespace Veyro.Desktop.Core.Groups;

public sealed class GroupStateChangedEventArgs(
    ulong epoch,
    string coordinatorDeviceId,
    IReadOnlyList<GroupMemberState> members,
    string reason) : EventArgs
{
    public ulong Epoch { get; } = epoch;

    public string CoordinatorDeviceId { get; } = coordinatorDeviceId;

    public IReadOnlyList<GroupMemberState> Members { get; } = members;

    public string Reason { get; } = reason;
}
