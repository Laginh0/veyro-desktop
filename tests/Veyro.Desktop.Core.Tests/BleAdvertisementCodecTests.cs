using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Tests;

public sealed class BleAdvertisementCodecTests
{
    [Fact]
    public void Advertisement_round_trips_in_compact_service_data()
    {
        var source = new VeyroBleAdvertisement(
            1,
            VeyroCapability.BleControl | VeyroCapability.WifiDirectData,
            "012345abcdef");

        var encoded = BleAdvertisementCodec.Encode(source);
        var decoded = BleAdvertisementCodec.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal(BleAdvertisementCodec.EncodedLength, encoded.Length);
        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void Advertisement_rejects_invalid_lengths(int length)
    {
        Assert.False(BleAdvertisementCodec.TryDecode(new byte[length], out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Ephemeral_id_is_random_and_has_twelve_hex_characters()
    {
        var first = BleAdvertisementCodec.CreateEphemeralId();
        var second = BleAdvertisementCodec.CreateEphemeralId();

        Assert.Matches("^[a-f0-9]{12}$", first);
        Assert.NotEqual(first, second);
    }
}
