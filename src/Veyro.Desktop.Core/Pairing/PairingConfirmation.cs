namespace Veyro.Desktop.Core.Pairing;

public sealed record PairingConfirmation(
    string PairingId,
    bool Accepted,
    byte[] VerificationDigest,
    byte[] Signature);
