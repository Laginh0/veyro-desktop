namespace Veyro.Desktop.Core.Discovery;

public sealed class DiscoveredDeviceRegistry(TimeSpan? retention = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, DiscoveredDevice> devices = new(StringComparer.Ordinal);
    private readonly TimeSpan retentionWindow = retention ?? TimeSpan.FromSeconds(20);

    public bool Observe(
        VeyroBleAdvertisement advertisement,
        ulong bluetoothAddress,
        short signalStrengthDbm,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        lock (sync)
        {
            var isNew = !devices.TryGetValue(advertisement.EphemeralId, out var existing);
            devices[advertisement.EphemeralId] = new DiscoveredDevice(
                advertisement.EphemeralId,
                bluetoothAddress,
                signalStrengthDbm,
                advertisement.Capabilities,
                advertisement.ProtocolMajor,
                existing?.FirstSeen ?? observedAt,
                observedAt);
            return isNew;
        }
    }

    public int RemoveExpired(DateTimeOffset now)
    {
        lock (sync)
        {
            var expired = devices
                .Where(pair => now - pair.Value.LastSeen > retentionWindow)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expired)
            {
                devices.Remove(key);
            }

            return expired.Length;
        }
    }

    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (sync)
        {
            return devices.Values
                .OrderByDescending(device => device.SignalStrengthDbm)
                .ThenBy(device => device.EphemeralId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
