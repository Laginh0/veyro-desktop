namespace Veyro.Desktop.Core.Identity;

public sealed record LocalIdentityKey(byte[] PrivateKeyPkcs8, byte[] PublicKeySpki);
