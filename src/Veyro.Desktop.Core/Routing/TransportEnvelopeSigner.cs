using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;
using Veyro.Protocol;

namespace Veyro.Desktop.Core.Routing;

public static class TransportEnvelopeSigner
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Veyro.TransportEnvelope.v1");

    public static void Sign(TransportEnvelope envelope, LocalIdentityKey identityKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(identityKey);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identityKey.PrivateKeyPkcs8, out _);
        envelope.OriginAuthentication = ByteString.CopyFrom(
            key.SignData(
                EncodeImmutableFields(envelope),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static bool Verify(TransportEnvelope envelope, TrustedDevice trustedDevice)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(trustedDevice);
        if (trustedDevice.IsRevoked ||
            !string.Equals(envelope.OriginDeviceId, trustedDevice.DeviceId, StringComparison.Ordinal) ||
            envelope.OriginAuthentication.IsEmpty)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trustedDevice.IdentityPublicKeyBase64), out _);
            return key.VerifyData(
                EncodeImmutableFields(envelope),
                envelope.OriginAuthentication.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static byte[] EncodeImmutableFields(TransportEnvelope envelope)
    {
        using var stream = new MemoryStream();
        stream.Write(Domain);
        WriteUInt32(stream, envelope.ProtocolMajor);
        WriteUInt32(stream, envelope.ProtocolMinor);
        Write(stream, envelope.MessageId);
        Write(stream, envelope.OriginDeviceId);
        WriteUInt32(stream, checked((uint)envelope.DestinationDeviceIds.Count));
        foreach (var destination in envelope.DestinationDeviceIds)
        {
            Write(stream, destination);
        }

        stream.WriteByte(envelope.AuthorizedBroadcast ? (byte)1 : (byte)0);
        WriteUInt32(stream, checked((uint)envelope.PayloadType));
        WriteInt64(stream, envelope.CreatedAtUnixMs);
        WriteInt64(stream, envelope.ExpiresAtUnixMs);
        WriteUInt64(stream, envelope.SequenceNumber);
        Write(stream, envelope.AcknowledgesMessageId);
        Write(stream, envelope.EncryptedPayload.Span);
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
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
