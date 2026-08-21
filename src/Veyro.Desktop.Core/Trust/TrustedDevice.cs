using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Trust;

public sealed record TrustedDevice(
    string DeviceId,
    string DisplayName,
    string IdentityPublicKeyBase64,
    VeyroCapability Capabilities,
    long TrustedAtUnixMilliseconds,
    long LastSeenAtUnixMilliseconds,
    long? RevokedAtUnixMilliseconds)
{
    public bool IsRevoked => RevokedAtUnixMilliseconds.HasValue;
}
