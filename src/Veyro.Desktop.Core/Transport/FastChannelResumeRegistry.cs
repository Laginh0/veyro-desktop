using System.Security.Cryptography;

namespace Veyro.Desktop.Core.Transport;

public sealed class FastChannelResumeRegistry(TimeSpan? retention = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, FastChannelResumeState> states = new(StringComparer.Ordinal);
    private readonly TimeSpan retentionWindow = retention ?? TimeSpan.FromMinutes(5);

    public FastChannelResumeState Create(string remoteDeviceId, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDeviceId);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var state = new FastChannelResumeState(
            Guid.NewGuid().ToString("N"),
            remoteDeviceId,
            RandomNumberGenerator.GetBytes(32),
            0,
            timestamp.Add(retentionWindow));
        lock (sync)
        {
            states[state.SessionId] = state;
        }

        return state;
    }

    public bool TryResume(
        string sessionId,
        string remoteDeviceId,
        ReadOnlySpan<byte> resumeToken,
        DateTimeOffset now,
        out FastChannelResumeState? state)
    {
        lock (sync)
        {
            if (!states.TryGetValue(sessionId, out state) ||
                state.ExpiresAt < now ||
                !string.Equals(state.RemoteDeviceId, remoteDeviceId, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(state.ResumeToken, resumeToken))
            {
                state = null;
                return false;
            }

            return true;
        }
    }

    public FastChannelResumeState? FindActiveForDevice(string remoteDeviceId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDeviceId);
        lock (sync)
        {
            return states.Values
                .Where(state =>
                    state.ExpiresAt >= now &&
                    string.Equals(state.RemoteDeviceId, remoteDeviceId, StringComparison.Ordinal))
                .OrderByDescending(state => state.ExpiresAt)
                .FirstOrDefault();
        }
    }

    public bool UpdateSequence(string sessionId, ulong lastReceivedSequence)
    {
        lock (sync)
        {
            if (!states.TryGetValue(sessionId, out var state) || lastReceivedSequence < state.LastReceivedSequence)
            {
                return false;
            }

            states[sessionId] = state with { LastReceivedSequence = lastReceivedSequence };
            return true;
        }
    }

    public int RemoveExpired(DateTimeOffset now)
    {
        lock (sync)
        {
            var expired = states.Values
                .Where(state => state.ExpiresAt < now)
                .Select(state => state.SessionId)
                .ToArray();
            foreach (var sessionId in expired)
            {
                states.Remove(sessionId);
            }

            return expired.Length;
        }
    }
}
