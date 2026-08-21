using System.Security.Cryptography;
using System.Text;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Trust;

public static class TrustedPeerAuthenticator
{
    private static readonly byte[] ChallengeLabel = Encoding.UTF8.GetBytes("Veyro.ReconnectChallenge.v1");

    public static byte[] CreateChallenge() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Sign(LocalIdentityKey identityKey, string deviceId, ReadOnlySpan<byte> challenge)
    {
        ArgumentNullException.ThrowIfNull(identityKey);
        ValidateChallenge(challenge);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identityKey.PrivateKeyPkcs8, out _);
        return key.SignData(
            CreatePayload(deviceId, challenge),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static bool Verify(TrustedDevice device, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateChallenge(challenge);
        if (device.IsRevoked)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.IdentityPublicKeyBase64), out _);
            return key.VerifyData(
                CreatePayload(device.DeviceId, challenge),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] CreatePayload(string deviceId, ReadOnlySpan<byte> challenge) =>
        [.. ChallengeLabel, .. Encoding.UTF8.GetBytes(deviceId), .. challenge];

    private static void ValidateChallenge(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 32)
        {
            throw new ArgumentException("A reconnect challenge must contain exactly 32 bytes.", nameof(challenge));
        }
    }
}
