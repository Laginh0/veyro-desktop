using Veyro.Protocol;

namespace Veyro.Desktop.Core.Routing;

public sealed record RoutingDecision(
    bool DeliverLocally,
    IReadOnlyList<string> ForwardTargets,
    TransportEnvelope? ForwardEnvelope,
    string? RejectionReason)
{
    public bool IsRejected => RejectionReason is not null;

    public static RoutingDecision Reject(string reason) => new(false, [], null, reason);
}
