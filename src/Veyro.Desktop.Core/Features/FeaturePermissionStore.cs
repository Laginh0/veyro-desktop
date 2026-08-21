using System.Security.Cryptography;
using System.Text.Json;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Features;

public sealed class FeaturePermissionStore(string filePath, IIdentityProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private List<FeaturePermissionEntry>? cache;

    public FeatureAccessPolicy GetPolicy(string deviceId, VeyroFeature feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            var entry = Load().SingleOrDefault(item =>
                string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal) && item.Feature == feature);
            return entry?.Policy ?? DefaultPolicy(feature);
        }
    }

    public void SetPolicy(string deviceId, VeyroFeature feature, FeatureAccessPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!Enum.IsDefined(feature) || !Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(feature));
        }

        lock (sync)
        {
            var entries = Load();
            entries.RemoveAll(item =>
                string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal) && item.Feature == feature);
            entries.Add(new FeaturePermissionEntry(deviceId, feature, policy));
            Save(entries);
        }
    }

    public void RemoveDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (sync)
        {
            var entries = Load();
            if (entries.RemoveAll(item =>
                    string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal)) > 0)
            {
                Save(entries);
            }
        }
    }

    private static FeatureAccessPolicy DefaultPolicy(VeyroFeature feature) =>
        feature is VeyroFeature.SecureCommands or VeyroFeature.RemoteInput
            ? FeatureAccessPolicy.Disabled
            : FeatureAccessPolicy.Ask;

    private List<FeaturePermissionEntry> Load()
    {
        if (cache is not null)
        {
            return [.. cache];
        }

        if (!File.Exists(filePath))
        {
            cache = [];
            return [];
        }

        var protectedBytes = File.ReadAllBytes(filePath);
        var plaintext = protector.Unprotect(protectedBytes);
        try
        {
            var entries = JsonSerializer.Deserialize<List<FeaturePermissionEntry>>(plaintext, JsonOptions) ?? [];
            if (entries.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.DeviceId) ||
                    !Enum.IsDefined(entry.Feature) ||
                    !Enum.IsDefined(entry.Policy)) ||
                entries.Select(entry => (entry.DeviceId, entry.Feature)).Distinct().Count() != entries.Count)
            {
                throw new InvalidDataException("The feature permission store is invalid.");
            }

            cache = entries;
            return [.. entries];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(List<FeaturePermissionEntry> entries)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The feature permission path has no parent directory.");
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        try
        {
            var protectedBytes = protector.Protect(plaintext);
            var temporaryFile = filePath + ".tmp";
            File.WriteAllBytes(temporaryFile, protectedBytes);
            File.Move(temporaryFile, filePath, true);
            cache = [.. entries];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private sealed record FeaturePermissionEntry(
        string DeviceId,
        VeyroFeature Feature,
        FeatureAccessPolicy Policy);
}
