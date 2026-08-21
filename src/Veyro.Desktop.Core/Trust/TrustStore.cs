using System.Security.Cryptography;
using System.Text.Json;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Trust;

public sealed class TrustStore(string trustFile, IIdentityProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();

    public IReadOnlyList<TrustedDevice> Snapshot()
    {
        lock (sync)
        {
            return Load()
                .OrderBy(device => device.IsRevoked)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public TrustedDevice? FindActive(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            return Load().SingleOrDefault(device =>
                !device.IsRevoked && string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        }
    }

    public void Trust(TrustedDevice trustedDevice)
    {
        ArgumentNullException.ThrowIfNull(trustedDevice);
        Validate(trustedDevice);
        lock (sync)
        {
            var devices = Load();
            devices.RemoveAll(device => string.Equals(device.DeviceId, trustedDevice.DeviceId, StringComparison.Ordinal));
            devices.Add(trustedDevice with { RevokedAtUnixMilliseconds = null });
            Save(devices);
        }
    }

    public bool Revoke(string deviceId, DateTimeOffset? revokedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            var devices = Load();
            var index = devices.FindIndex(device =>
                !device.IsRevoked && string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            devices[index] = devices[index] with
            {
                RevokedAtUnixMilliseconds = (revokedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()
            };
            Save(devices);
            return true;
        }
    }

    public bool MarkSeen(string deviceId, DateTimeOffset? seenAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            var devices = Load();
            var index = devices.FindIndex(device =>
                !device.IsRevoked && string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            devices[index] = devices[index] with
            {
                LastSeenAtUnixMilliseconds = (seenAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()
            };
            Save(devices);
            return true;
        }
    }

    private List<TrustedDevice> Load()
    {
        if (!File.Exists(trustFile))
        {
            return [];
        }

        var protectedBytes = File.ReadAllBytes(trustFile);
        var plaintext = protector.Unprotect(protectedBytes);
        try
        {
            var devices = JsonSerializer.Deserialize<List<TrustedDevice>>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The trust store is empty.");
            foreach (var device in devices)
            {
                Validate(device);
            }

            if (devices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != devices.Count)
            {
                throw new InvalidDataException("The trust store contains duplicate device IDs.");
            }

            return devices;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(List<TrustedDevice> devices)
    {
        var directory = Path.GetDirectoryName(trustFile)
            ?? throw new InvalidOperationException("The trust store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(devices, JsonOptions);
        try
        {
            var protectedBytes = protector.Protect(plaintext);
            var temporaryFile = trustFile + ".tmp";
            File.WriteAllBytes(temporaryFile, protectedBytes);
            File.Move(temporaryFile, trustFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(TrustedDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.DisplayName))
        {
            throw new InvalidDataException("A trusted device is missing identity fields.");
        }

        try
        {
            var publicKey = Convert.FromBase64String(device.IdentityPublicKeyBase64);
            using var identityKey = ECDsa.Create();
            identityKey.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException("A trusted device contains an invalid identity public key.", exception);
        }
    }
}
