using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Transport;

public static class FastChannelOfferSigner
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Veyro.FastChannelOffer.v1");

    public static Veyro.Protocol.FastChannelOffer Create(
        LocalIdentity identity,
        LocalIdentityKey identityKey,
        Veyro.Protocol.FastChannelRole role,
        ushort tcpPort,
        FastChannelResumeState resumeState)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identityKey);
        ArgumentNullException.ThrowIfNull(resumeState);
        if (role == Veyro.Protocol.FastChannelRole.Unspecified || tcpPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var offer = new Veyro.Protocol.FastChannelOffer
        {
            SessionId = resumeState.SessionId,
            DeviceId = identity.DeviceId,
            Role = role,
            TcpPort = tcpPort,
            TlsAlpn = "veyro/1",
            ResumeToken = Google.Protobuf.ByteString.CopyFrom(resumeState.ResumeToken)
        };
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identityKey.PrivateKeyPkcs8, out _);
        offer.Signature = Google.Protobuf.ByteString.CopyFrom(
            key.SignData(Encode(offer), HashAlgorithmName.SHA256));
        return offer;
    }

    public static bool Validate(Veyro.Protocol.FastChannelOffer offer, TrustedDevice trustedDevice)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(trustedDevice);
        if (trustedDevice.IsRevoked ||
            !string.Equals(offer.DeviceId, trustedDevice.DeviceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(offer.SessionId) ||
            offer.Role == Veyro.Protocol.FastChannelRole.Unspecified ||
            offer.TcpPort is 0 or > ushort.MaxValue ||
            !string.Equals(offer.TlsAlpn, "veyro/1", StringComparison.Ordinal) ||
            offer.ResumeToken.Length != 32)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trustedDevice.IdentityPublicKeyBase64), out _);
            return key.VerifyData(Encode(offer), offer.Signature.Span, HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static byte[] Encode(Veyro.Protocol.FastChannelOffer offer)
    {
        using var stream = new MemoryStream();
        stream.Write(Domain);
        Write(stream, offer.SessionId);
        Write(stream, offer.DeviceId);
        WriteUInt32(stream, (uint)offer.Role);
        WriteUInt32(stream, offer.TcpPort);
        Write(stream, offer.TlsAlpn);
        Write(stream, offer.ResumeToken.Span);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value) => Write(stream, Encoding.UTF8.GetBytes(value));

    private static void Write(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        stream.Write(encoded);
    }
}
