using System.Security.Cryptography;
using System.Text.Json;

namespace Veyro.Desktop.Core.Identity;

public sealed class LocalIdentityKeyStore(string keyFile, IIdentityProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LocalIdentityKey LoadOrCreate()
    {
        if (File.Exists(keyFile))
        {
            return Load();
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new LocalIdentityKey(
            key.ExportPkcs8PrivateKey(),
            key.ExportSubjectPublicKeyInfo());
        Save(material);
        return material;
    }

    private LocalIdentityKey Load()
    {
        var protectedBytes = File.ReadAllBytes(keyFile);
        var plaintext = protector.Unprotect(protectedBytes);
        try
        {
            var material = JsonSerializer.Deserialize<LocalIdentityKey>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The local identity key is empty.");
            Validate(material);
            return material;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(LocalIdentityKey material)
    {
        var directory = Path.GetDirectoryName(keyFile)
            ?? throw new InvalidOperationException("The identity key path has no parent directory.");
        Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(material, JsonOptions);
        try
        {
            var protectedBytes = protector.Protect(plaintext);
            var temporaryFile = keyFile + ".tmp";
            File.WriteAllBytes(temporaryFile, protectedBytes);
            File.Move(temporaryFile, keyFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(LocalIdentityKey material)
    {
        using var privateKey = ECDsa.Create();
        privateKey.ImportPkcs8PrivateKey(material.PrivateKeyPkcs8, out _);
        if (!CryptographicOperations.FixedTimeEquals(
                privateKey.ExportSubjectPublicKeyInfo(),
                material.PublicKeySpki))
        {
            throw new InvalidDataException("The local identity key pair is inconsistent.");
        }
    }
}
