namespace Veyro.Desktop.Core.Groups;

public enum GroupControlKind
{
    MembershipSnapshot = 1,
    ElectionStarted = 2,
    CoordinatorCommitted = 3
}

public sealed record GroupControlMessage(
    int Version,
    GroupControlKind Kind,
    ulong Epoch,
    string CoordinatorDeviceId,
    string InitiatorDeviceId,
    string Reason,
    IReadOnlyList<GroupMemberState> Members);
