namespace Veyro.Desktop.Core.Identity;

public interface IIdentityProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
