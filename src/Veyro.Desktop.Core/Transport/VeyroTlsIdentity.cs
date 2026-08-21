using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Transport;

public static class VeyroTlsIdentity
{
    public static readonly SslApplicationProtocol ApplicationProtocol = new("veyro/1");

    public static X509Certificate2 CreateCertificate(
        LocalIdentity identity,
        LocalIdentityKey identityKey,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identityKey);

        using var signingKey = ECDsa.Create();
        signingKey.ImportPkcs8PrivateKey(identityKey.PrivateKeyPkcs8, out _);
        var request = new CertificateRequest(
            $"CN={identity.DeviceId}",
            signingKey,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"),
                new("1.3.6.1.5.5.7.3.2")
            },
            true));

        var timestamp = now ?? DateTimeOffset.UtcNow;
        using var inMemoryCertificate = request.CreateSelfSigned(
            timestamp.AddMinutes(-5),
            timestamp.AddYears(2));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pkcs12 = inMemoryCertificate.Export(X509ContentType.Pkcs12, password);
        try
        {
            // Schannel requires an OS-backed CNG key for server-side TLS on Windows.
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password,
                X509KeyStorageFlags.UserKeySet |
                X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    public static bool ValidatePeerCertificate(
        X509Certificate? certificate,
        TrustedDevice trustedDevice,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(trustedDevice);
        if (certificate is null || trustedDevice.IsRevoked)
        {
            return false;
        }

        try
        {
            using var peerCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var timestamp = now ?? DateTimeOffset.UtcNow;
            if (timestamp.UtcDateTime < peerCertificate.NotBefore.ToUniversalTime() ||
                timestamp.UtcDateTime > peerCertificate.NotAfter.ToUniversalTime() ||
                !string.Equals(
                    peerCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    trustedDevice.DeviceId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            using var publicKey = peerCertificate.GetECDsaPublicKey();
            if (publicKey is null)
            {
                return false;
            }

            var expectedPublicKey = Convert.FromBase64String(trustedDevice.IdentityPublicKeyBase64);
            return CryptographicOperations.FixedTimeEquals(
                expectedPublicKey,
                publicKey.ExportSubjectPublicKeyInfo());
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
