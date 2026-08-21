using Veyro.Desktop.Core.Pairing;

namespace Veyro.Desktop.Pairing;

public sealed class PairingPinEventArgs(PairingVerification verification) : EventArgs
{
    public PairingVerification Verification { get; } = verification;
}
