using System.Security.Cryptography;
using System.Text.Json;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Transport;

public sealed class FastChannelResumeRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly Dictionary<string, FastChannelResumeState> states = new(StringComparer.Ordinal);
    private readonly TimeSpan retentionWindow;
    private readonly string? stateFile;
    private readonly IIdentityProtector? protector;

    public FastChannelResumeRegistry(
        TimeSpan? retention = null,
        string? stateFile = null,
        IIdentityProtector? protector = null)
    {
        retentionWindow = retention ?? TimeSpan.FromHours(24);
        if (retentionWindow <= TimeSpan.Zero || retentionWindow > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        if ((stateFile is null) != (protector is null))
        {
            throw new ArgumentException("Persistent resume state requires both a path and a protector.");
        }

        this.stateFile = stateFile;
        this.protector = protector;
        foreach (var state in Load())
        {
            states[state.SessionId] = state;
        }

        RemoveExpired(DateTimeOffset.UtcNow);
    }

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
            foreach (var stale in states.Values
                .Where(item => string.Equals(item.RemoteDeviceId, remoteDeviceId, StringComparison.Ordinal))
                .Select(item => item.SessionId)
                .ToArray())
            {
                if (states.Remove(stale, out var removed))
                {
                    CryptographicOperations.ZeroMemory(removed.ResumeToken);
                }
            }

            states[state.SessionId] = state;
            SaveLocked();
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

            state = state with { ExpiresAt = now.Add(retentionWindow) };
            states[sessionId] = state;
            SaveLocked();
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
            SaveLocked();
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
                if (states.Remove(sessionId, out var state))
                {
                    CryptographicOperations.ZeroMemory(state.ResumeToken);
                }
            }

            if (expired.Length > 0)
            {
                SaveLocked();
            }

            return expired.Length;
        }
    }

    public int RemoveDevice(string remoteDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDeviceId);
        lock (sync)
        {
            var sessionIds = states.Values
                .Where(state => string.Equals(state.RemoteDeviceId, remoteDeviceId, StringComparison.Ordinal))
                .Select(state => state.SessionId)
                .ToArray();
            foreach (var sessionId in sessionIds)
            {
                if (states.Remove(sessionId, out var state))
                {
                    CryptographicOperations.ZeroMemory(state.ResumeToken);
                }
            }

            if (sessionIds.Length > 0)
            {
                SaveLocked();
            }

            return sessionIds.Length;
        }
    }

    private IReadOnlyList<FastChannelResumeState> Load()
    {
        if (stateFile is null || protector is null || !File.Exists(stateFile))
        {
            return [];
        }

        var plaintext = protector.Unprotect(File.ReadAllBytes(stateFile));
        try
        {
            var loaded = JsonSerializer.Deserialize<List<FastChannelResumeState>>(plaintext, JsonOptions) ?? [];
            if (loaded.Any(state =>
                    !Guid.TryParseExact(state.SessionId, "N", out _) ||
                    string.IsNullOrWhiteSpace(state.RemoteDeviceId) ||
                    state.ResumeToken.Length != 32) ||
                loaded.Select(state => state.SessionId).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
            {
                throw new InvalidDataException("The persisted resume registry is invalid.");
            }

            return loaded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void SaveLocked()
    {
        if (stateFile is null || protector is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(states.Values, JsonOptions);
        try
        {
            var temporaryFile = stateFile + ".tmp";
            File.WriteAllBytes(temporaryFile, protector.Protect(plaintext));
            File.Move(temporaryFile, stateFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
