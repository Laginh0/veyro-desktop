using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Pairing;

public sealed class PairingSession : IDisposable
{
    private static readonly byte[] VerificationLabel = Encoding.UTF8.GetBytes("Veyro.PairingVerification.v1");
    private static readonly byte[] HelloLabel = Encoding.UTF8.GetBytes("Veyro.PairingHello.v1");
    private static readonly byte[] ConfirmationLabel = Encoding.UTF8.GetBytes("Veyro.PairingConfirmation.v1");
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);
    private readonly ECDiffieHellman ephemeralKey;
    private readonly LocalIdentityKey localIdentityKey;
    private PairingHello? remoteHello;
    private byte[]? verificationDigest;
    private bool localAccepted;
    private bool remoteAccepted;

    private PairingSession(
        LocalIdentity identity,
        LocalIdentityKey identityKey,
        VeyroCapability capabilities,
        string pairingId,
        DateTimeOffset createdAt)
    {
        localIdentityKey = identityKey;
        ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var unsigned = new PairingHello(
            pairingId,
            identity.DeviceId,
            identity.DisplayName,
            capabilities,
            createdAt.ToUnixTimeMilliseconds(),
            RandomNumberGenerator.GetBytes(32),
            identityKey.PublicKeySpki,
            ephemeralKey.ExportSubjectPublicKeyInfo(),
            []);
        LocalHello = unsigned with { Signature = Sign(identityKey.PrivateKeyPkcs8, EncodeHello(unsigned)) };
    }

    public PairingHello LocalHello { get; }

    public bool IsMutuallyConfirmed => localAccepted && remoteAccepted;

    public static PairingSession Create(
        LocalIdentity identity,
        LocalIdentityKey identityKey,
        VeyroCapability capabilities,
        string? pairingId = null,
        DateTimeOffset? createdAt = null) =>
        new(
            identity,
            identityKey,
            capabilities,
            pairingId ?? Guid.NewGuid().ToString("N"),
            createdAt ?? DateTimeOffset.UtcNow);

    public PairingVerification AcceptRemoteHello(PairingHello remote, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (!string.Equals(LocalHello.PairingId, remote.PairingId, StringComparison.Ordinal))
        {
            throw new PairingProtocolException("The pairing session IDs do not match.");
        }

        if (string.Equals(LocalHello.DeviceId, remote.DeviceId, StringComparison.Ordinal))
        {
            throw new PairingProtocolException("A device cannot pair with itself.");
        }

        var observedAt = now ?? DateTimeOffset.UtcNow;
        var remoteCreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(remote.CreatedAtUnixMilliseconds);
        if ((observedAt - remoteCreatedAt).Duration() > MaximumClockSkew)
        {
            throw new PairingProtocolException("The pairing hello is outside the accepted time window.");
        }

        if (remote.Nonce.Length != 32 || remote.IdentityPublicKeySpki.Length == 0 || remote.EphemeralPublicKeySpki.Length == 0)
        {
            throw new PairingProtocolException("The pairing hello contains invalid key material.");
        }

        if (!Verify(remote.IdentityPublicKeySpki, EncodeHello(remote), remote.Signature))
        {
            throw new PairingProtocolException("The pairing hello signature is invalid.");
        }

        using var remoteEphemeralKey = ECDiffieHellman.Create();
        try
        {
            remoteEphemeralKey.ImportSubjectPublicKeyInfo(remote.EphemeralPublicKeySpki, out _);
        }
        catch (CryptographicException exception)
        {
            throw new PairingProtocolException($"The remote ephemeral key is invalid: {exception.Message}");
        }

        var rawSharedSecret = ephemeralKey.DeriveRawSecretAgreement(remoteEphemeralKey.PublicKey);
        var sharedSecret = SHA256.HashData(rawSharedSecret);
        try
        {
            var transcript = CreateTranscript(LocalHello, remote);
            byte[] verificationPayload = [.. VerificationLabel, .. transcript];
            verificationDigest = HMACSHA256.HashData(sharedSecret, verificationPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawSharedSecret);
            CryptographicOperations.ZeroMemory(sharedSecret);
        }

        remoteHello = remote;
        var pinValue = BinaryPrimitives.ReadUInt32BigEndian(verificationDigest) % 1_000_000;
        return new PairingVerification(pinValue.ToString("D6"), remote.DeviceId, remote.DisplayName);
    }

    public PairingConfirmation CreateConfirmation(bool accepted)
    {
        EnsureNegotiated();
        localAccepted = accepted;
        var unsigned = new PairingConfirmation(
            LocalHello.PairingId,
            accepted,
            verificationDigest!,
            []);
        return unsigned with
        {
            Signature = Sign(localIdentityKey.PrivateKeyPkcs8, EncodeConfirmation(unsigned))
        };
    }

    public void AcceptRemoteConfirmation(PairingConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        EnsureNegotiated();
        if (!string.Equals(LocalHello.PairingId, confirmation.PairingId, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(verificationDigest!, confirmation.VerificationDigest) ||
            !Verify(remoteHello!.IdentityPublicKeySpki, EncodeConfirmation(confirmation), confirmation.Signature))
        {
            throw new PairingProtocolException("The remote pairing confirmation is invalid.");
        }

        remoteAccepted = confirmation.Accepted;
    }

    public TrustedDevice CreateTrustedDevice(DateTimeOffset? trustedAt = null)
    {
        EnsureNegotiated();
        if (!IsMutuallyConfirmed)
        {
            throw new InvalidOperationException("Both devices must confirm the verification PIN.");
        }

        var timestamp = (trustedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        return new TrustedDevice(
            remoteHello!.DeviceId,
            remoteHello.DisplayName,
            Convert.ToBase64String(remoteHello.IdentityPublicKeySpki),
            remoteHello.Capabilities,
            timestamp,
            timestamp,
            null);
    }

    private void EnsureNegotiated()
    {
        if (remoteHello is null || verificationDigest is null)
        {
            throw new InvalidOperationException("A valid remote hello is required first.");
        }
    }

    private static byte[] CreateTranscript(PairingHello first, PairingHello second)
    {
        var hellos = new[] { first, second }
            .OrderBy(hello => hello.DeviceId, StringComparer.Ordinal)
            .Select(EncodeHello)
            .ToArray();
        return [.. hellos[0], .. hellos[1]];
    }

    private static byte[] EncodeHello(PairingHello hello)
    {
        using var stream = new MemoryStream();
        stream.Write(HelloLabel);
        Write(stream, hello.PairingId);
        Write(stream, hello.DeviceId);
        Write(stream, hello.DisplayName);
        stream.WriteByte((byte)hello.Capabilities);
        WriteInt64(stream, hello.CreatedAtUnixMilliseconds);
        Write(stream, hello.Nonce);
        Write(stream, hello.IdentityPublicKeySpki);
        Write(stream, hello.EphemeralPublicKeySpki);
        return stream.ToArray();
    }

    private static byte[] EncodeConfirmation(PairingConfirmation confirmation)
    {
        using var stream = new MemoryStream();
        stream.Write(ConfirmationLabel);
        Write(stream, confirmation.PairingId);
        stream.WriteByte(confirmation.Accepted ? (byte)1 : (byte)0);
        Write(stream, confirmation.VerificationDigest);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value) => Write(stream, Encoding.UTF8.GetBytes(value));

    private static void Write(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        stream.Write(length);
        stream.Write(value);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        stream.Write(encoded);
    }

    private static byte[] Sign(byte[] privateKeyPkcs8, byte[] data)
    {
        using var identityKey = ECDsa.Create();
        identityKey.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        return identityKey.SignData(
            data,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        try
        {
            using var identityKey = ECDsa.Create();
            identityKey.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return identityKey.VerifyData(
                data,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        ephemeralKey.Dispose();
        if (verificationDigest is not null)
        {
            CryptographicOperations.ZeroMemory(verificationDigest);
        }
    }
}
