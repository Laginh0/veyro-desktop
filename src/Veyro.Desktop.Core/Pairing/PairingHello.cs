using Veyro.Desktop.Core.Discovery;

namespace Veyro.Desktop.Core.Pairing;

public sealed record PairingHello(
    string PairingId,
    string DeviceId,
    string DisplayName,
    VeyroCapability Capabilities,
    long CreatedAtUnixMilliseconds,
    byte[] Nonce,
    byte[] IdentityPublicKeySpki,
    byte[] EphemeralPublicKeySpki,
    byte[] Signature);
