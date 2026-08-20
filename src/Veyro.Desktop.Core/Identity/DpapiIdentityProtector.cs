using System.Security.Cryptography;
using System.Text;

namespace Veyro.Desktop.Core.Identity;

public sealed class DpapiIdentityProtector : IIdentityProtector
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("Veyro.LocalIdentity.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), OptionalEntropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) =>
        ProtectedData.Unprotect(ciphertext.ToArray(), OptionalEntropy, DataProtectionScope.CurrentUser);
}
