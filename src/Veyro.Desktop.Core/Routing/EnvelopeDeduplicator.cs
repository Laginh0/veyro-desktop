namespace Veyro.Desktop.Core.Routing;

public sealed class EnvelopeDeduplicator(int maximumEntries = 4096)
{
    private readonly object sync = new();
    private readonly Dictionary<string, long> expirations = new(StringComparer.Ordinal);

    public bool TryRemember(string messageId, long expiresAtUnixMilliseconds, long nowUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (sync)
        {
            RemoveExpired(nowUnixMilliseconds);
            if (expirations.ContainsKey(messageId))
            {
                return false;
            }

            if (expirations.Count >= maximumEntries)
            {
                var oldest = expirations.MinBy(pair => pair.Value);
                expirations.Remove(oldest.Key);
            }

            expirations[messageId] = expiresAtUnixMilliseconds;
            return true;
        }
    }

    public int RemoveExpired(long nowUnixMilliseconds)
    {
        lock (sync)
        {
            var expired = expirations
                .Where(pair => pair.Value < nowUnixMilliseconds)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var messageId in expired)
            {
                expirations.Remove(messageId);
            }

            return expired.Length;
        }
    }
}
