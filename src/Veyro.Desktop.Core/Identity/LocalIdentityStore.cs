using System.Security.Cryptography;
using System.Text.Json;

namespace Veyro.Desktop.Core.Identity;

public sealed class LocalIdentityStore(string identityFile, IIdentityProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LocalIdentity LoadOrCreate()
    {
        if (File.Exists(identityFile))
        {
            return Load();
        }

        var identity = new LocalIdentity(
            CreateDeviceId(),
            CreateDisplayName(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Save(identity);
        return identity;
    }

    private LocalIdentity Load()
    {
        var protectedBytes = File.ReadAllBytes(identityFile);
        var plaintext = protector.Unprotect(protectedBytes);
        try
        {
            return JsonSerializer.Deserialize<LocalIdentity>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The local identity is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(LocalIdentity identity)
    {
        var directory = Path.GetDirectoryName(identityFile)
            ?? throw new InvalidOperationException("The identity path has no parent directory.");
        Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        try
        {
            var protectedBytes = protector.Protect(plaintext);
            var temporaryFile = identityFile + ".tmp";
            File.WriteAllBytes(temporaryFile, protectedBytes);
            File.Move(temporaryFile, identityFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string CreateDeviceId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string CreateDisplayName()
    {
        var machineName = Environment.MachineName.Trim();
        return string.IsNullOrWhiteSpace(machineName)
            ? "Veyro - Windows"
            : $"Veyro - {machineName}";
    }
}
