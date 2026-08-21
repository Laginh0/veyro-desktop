using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Tests;

public sealed class DiscoveredDeviceRegistryTests
{
    [Fact]
    public void Observation_updates_signal_without_changing_first_seen()
    {
        var registry = new DiscoveredDeviceRegistry(TimeSpan.FromSeconds(10));
        var advertisement = new VeyroBleAdvertisement(1, VeyroCapability.BleControl, "012345abcdef");
        var firstSeen = DateTimeOffset.Parse("2026-08-20T12:00:00Z");

        Assert.True(registry.Observe(advertisement, 10, -70, firstSeen));
        Assert.False(registry.Observe(advertisement, 10, -45, firstSeen.AddSeconds(2)));

        var device = Assert.Single(registry.Snapshot());
        Assert.Equal(firstSeen, device.FirstSeen);
        Assert.Equal(firstSeen.AddSeconds(2), device.LastSeen);
        Assert.Equal(-45, device.SignalStrengthDbm);
    }

    [Fact]
    public void Expired_observations_are_removed()
    {
        var registry = new DiscoveredDeviceRegistry(TimeSpan.FromSeconds(10));
        var observedAt = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        registry.Observe(
            new VeyroBleAdvertisement(1, VeyroCapability.BleControl, "012345abcdef"),
            10,
            -50,
            observedAt);

        Assert.Equal(0, registry.RemoveExpired(observedAt.AddSeconds(10)));
        Assert.Equal(1, registry.RemoveExpired(observedAt.AddSeconds(11)));
        Assert.Empty(registry.Snapshot());
    }
}
