using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;
using Veyro.Protocol;

namespace Veyro.Desktop.Core.Security;

public static class ApplicationPayloadCipher
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Veyro.ApplicationPayload.v1");

    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        string originDeviceId,
        IReadOnlyCollection<TrustedDevice> recipients)
    {
        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("The application payload cannot be empty.", nameof(plaintext));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0 ||
            recipients.Select(recipient => recipient.DeviceId).Distinct(StringComparer.Ordinal).Count() !=
            recipients.Count)
        {
            throw new ArgumentException("At least one unique recipient is required.", nameof(recipients));
        }

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var encrypted = new EncryptedApplicationPayload
        {
            EphemeralPublicKeySpki = ByteString.CopyFrom(ephemeralKey.ExportSubjectPublicKeyInfo())
        };

        foreach (var recipient in recipients.OrderBy(item => item.DeviceId, StringComparer.Ordinal))
        {
            if (recipient.IsRevoked)
            {
                throw new CryptographicException("A revoked device cannot receive an application payload.");
            }

            using var remoteSigningKey = ECDsa.Create();
            remoteSigningKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(recipient.IdentityPublicKeyBase64),
                out _);
            using var remoteAgreementKey = ECDiffieHellman.Create(remoteSigningKey.ExportParameters(false));
            var sharedSecret = ephemeralKey.DeriveKeyMaterial(remoteAgreementKey.PublicKey);
            var encryptionKey = DeriveEncryptionKey(sharedSecret, originDeviceId, recipient.DeviceId);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceLength);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagLength];
                using var aes = new AesGcm(encryptionKey, TagLength);
                aes.Encrypt(
                    nonce,
                    plaintext,
                    ciphertext,
                    tag,
                    CreateAssociatedData(originDeviceId, recipient.DeviceId));
                encrypted.Recipients.Add(
                    new RecipientCiphertext
                    {
                        DestinationDeviceId = recipient.DeviceId,
                        Nonce = ByteString.CopyFrom(nonce),
                        Ciphertext = ByteString.CopyFrom(ciphertext),
                        AuthenticationTag = ByteString.CopyFrom(tag)
                    });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }

        return encrypted.ToByteArray();
    }

    public static byte[] Decrypt(
        ReadOnlySpan<byte> encryptedPayload,
        string originDeviceId,
        string localDeviceId,
        LocalIdentityKey localIdentityKey)
    {
        if (encryptedPayload.IsEmpty)
        {
            throw new ArgumentException("The encrypted application payload cannot be empty.", nameof(encryptedPayload));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);
        ArgumentNullException.ThrowIfNull(localIdentityKey);

        EncryptedApplicationPayload encrypted;
        try
        {
            encrypted = EncryptedApplicationPayload.Parser.ParseFrom(encryptedPayload);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new CryptographicException("The encrypted application payload is malformed.", exception);
        }

        var recipient = encrypted.Recipients.SingleOrDefault(item =>
            string.Equals(item.DestinationDeviceId, localDeviceId, StringComparison.Ordinal))
            ?? throw new CryptographicException("The application payload is not addressed to this device.");
        if (encrypted.EphemeralPublicKeySpki.IsEmpty ||
            recipient.Nonce.Length != NonceLength ||
            recipient.AuthenticationTag.Length != TagLength ||
            recipient.Ciphertext.IsEmpty)
        {
            throw new CryptographicException("The encrypted application payload has invalid cryptographic fields.");
        }

        using var localSigningKey = ECDsa.Create();
        localSigningKey.ImportPkcs8PrivateKey(localIdentityKey.PrivateKeyPkcs8, out _);
        using var localAgreementKey = ECDiffieHellman.Create(localSigningKey.ExportParameters(true));
        using var ephemeralKey = ECDiffieHellman.Create();
        ephemeralKey.ImportSubjectPublicKeyInfo(encrypted.EphemeralPublicKeySpki.Span, out _);
        var sharedSecret = localAgreementKey.DeriveKeyMaterial(ephemeralKey.PublicKey);
        var encryptionKey = DeriveEncryptionKey(sharedSecret, originDeviceId, localDeviceId);
        try
        {
            var plaintext = new byte[recipient.Ciphertext.Length];
            using var aes = new AesGcm(encryptionKey, TagLength);
            aes.Decrypt(
                recipient.Nonce.Span,
                recipient.Ciphertext.Span,
                recipient.AuthenticationTag.Span,
                plaintext,
                CreateAssociatedData(originDeviceId, localDeviceId));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    private static byte[] DeriveEncryptionKey(
        byte[] sharedSecret,
        string originDeviceId,
        string destinationDeviceId) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            KeyLength,
            salt: Domain,
            info: CreateAssociatedData(originDeviceId, destinationDeviceId));

    private static byte[] CreateAssociatedData(string originDeviceId, string destinationDeviceId) =>
        Encoding.UTF8.GetBytes($"Veyro.ApplicationPayload.v1\0{originDeviceId}\0{destinationDeviceId}");
}
