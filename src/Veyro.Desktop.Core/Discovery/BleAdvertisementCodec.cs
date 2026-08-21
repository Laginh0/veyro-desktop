using System.Security.Cryptography;

namespace Veyro.Desktop.Core.Discovery;

public static class BleAdvertisementCodec
{
    public const int EncodedLength = 8;
    public const byte SupportedProtocolMajor = 1;

    public static string CreateEphemeralId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static byte[] Encode(VeyroBleAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (advertisement.ProtocolMajor == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(advertisement), "Protocol major must be non-zero.");
        }

        byte[] ephemeralBytes;
        try
        {
            ephemeralBytes = Convert.FromHexString(advertisement.EphemeralId);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The ephemeral ID must be hexadecimal.", nameof(advertisement), exception);
        }

        if (ephemeralBytes.Length != 6)
        {
            throw new ArgumentException("The ephemeral ID must contain exactly 12 hexadecimal characters.", nameof(advertisement));
        }

        var encoded = new byte[EncodedLength];
        encoded[0] = advertisement.ProtocolMajor;
        encoded[1] = (byte)advertisement.Capabilities;
        ephemeralBytes.CopyTo(encoded, 2);
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out VeyroBleAdvertisement? advertisement)
    {
        advertisement = null;
        if (encoded.Length != EncodedLength || encoded[0] == 0)
        {
            return false;
        }

        advertisement = new VeyroBleAdvertisement(
            encoded[0],
            (VeyroCapability)encoded[1],
            Convert.ToHexStringLower(encoded[2..]));
        return true;
    }
}
