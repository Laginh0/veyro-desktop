using Veyro.Desktop.Core.Protocol;
using Veyro.Desktop.Core.Trust;
using Veyro.Protocol;

namespace Veyro.Desktop.Core.Routing;

public sealed class LogicalRouter(
    string localDeviceId,
    EnvelopeDeduplicator deduplicator,
    Func<string, TrustedDevice?> trustedDeviceResolver)
{
    public RoutingDecision RouteIncoming(
        TransportEnvelope envelope,
        string ingressDeviceId,
        IReadOnlyCollection<string> connectedDeviceIds,
        bool localIsCoordinator,
        long nowUnixMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(ingressDeviceId);
        ArgumentNullException.ThrowIfNull(connectedDeviceIds);

        var validation = TransportEnvelopeValidator.Validate(envelope, nowUnixMilliseconds);
        if (!validation.IsValid)
        {
            return RoutingDecision.Reject(validation.ErrorCode!);
        }

        if (envelope.DestinationDeviceIds.Distinct(StringComparer.Ordinal).Count() !=
            envelope.DestinationDeviceIds.Count)
        {
            return RoutingDecision.Reject("duplicate_destination");
        }

        var trustedOrigin = trustedDeviceResolver(envelope.OriginDeviceId);
        if (trustedOrigin is null || !TransportEnvelopeSigner.Verify(envelope, trustedOrigin))
        {
            return RoutingDecision.Reject("invalid_origin_authentication");
        }

        if (!deduplicator.TryRemember(envelope.MessageId, envelope.ExpiresAtUnixMs, nowUnixMilliseconds))
        {
            return RoutingDecision.Reject("duplicate_message");
        }

        var deliverLocally = envelope.AuthorizedBroadcast ||
            envelope.DestinationDeviceIds.Contains(localDeviceId, StringComparer.Ordinal);
        if (!localIsCoordinator || envelope.RemainingHops <= 1)
        {
            return new RoutingDecision(deliverLocally, [], null, null);
        }

        var connected = connectedDeviceIds.ToHashSet(StringComparer.Ordinal);
        connected.Remove(ingressDeviceId);
        connected.Remove(envelope.OriginDeviceId);
        connected.Remove(localDeviceId);

        string[] forwardTargets;
        if (envelope.AuthorizedBroadcast)
        {
            forwardTargets = connected.Order(StringComparer.Ordinal).ToArray();
        }
        else
        {
            forwardTargets = envelope.DestinationDeviceIds
                .Where(destination => connected.Contains(destination))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        if (forwardTargets.Length == 0)
        {
            return new RoutingDecision(deliverLocally, [], null, null);
        }

        var forwarded = envelope.Clone();
        forwarded.RemainingHops--;
        return new RoutingDecision(deliverLocally, forwardTargets, forwarded, null);
    }

    public IReadOnlyList<string> PlanOutboundTargets(
        TransportEnvelope envelope,
        IReadOnlyCollection<string> connectedDeviceIds,
        bool localIsCoordinator,
        string? coordinatorDeviceId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var connected = connectedDeviceIds.ToHashSet(StringComparer.Ordinal);
        if (localIsCoordinator || envelope.AuthorizedBroadcast)
        {
            return envelope.AuthorizedBroadcast
                ? connected.Order(StringComparer.Ordinal).ToArray()
                : envelope.DestinationDeviceIds
                    .Where(connected.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
        }

        var directTargets = envelope.DestinationDeviceIds
            .Where(connected.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (directTargets.Length == envelope.DestinationDeviceIds.Count)
        {
            return directTargets;
        }

        return coordinatorDeviceId is not null && connected.Contains(coordinatorDeviceId)
            ? [coordinatorDeviceId]
            : directTargets;
    }
}
