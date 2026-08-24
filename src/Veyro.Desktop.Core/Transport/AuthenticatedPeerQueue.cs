using System.Collections.Concurrent;

namespace Veyro.Desktop.Core.Transport;

public sealed class AuthenticatedPeerQueue
{
    private readonly ConcurrentQueue<string> queue = new();
    private readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.Ordinal);

    public bool Enqueue(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!queued.TryAdd(deviceId, 0))
        {
            return false;
        }

        queue.Enqueue(deviceId);
        return true;
    }

    public string? Claim(Func<string, bool> isEligible)
    {
        ArgumentNullException.ThrowIfNull(isEligible);
        while (queue.TryDequeue(out var deviceId))
        {
            queued.TryRemove(deviceId, out _);
            if (isEligible(deviceId))
            {
                return deviceId;
            }
        }

        return null;
    }
}
