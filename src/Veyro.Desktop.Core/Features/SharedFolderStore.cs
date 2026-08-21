using System.Security.Cryptography;
using System.Text.Json;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Features;

public sealed class SharedFolderStore(string filePath, IIdentityProtector protector)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();

    public IReadOnlyList<SharedFolder> Snapshot()
    {
        lock (sync)
        {
            return Load().OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public SharedFolder Add(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);
        var info = new DirectoryInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new DirectoryNotFoundException("A pasta não existe ou é um redirecionamento não permitido.");
        }

        lock (sync)
        {
            var folders = Load();
            var existing = folders.SingleOrDefault(item =>
                string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var folder = new SharedFolder(Guid.NewGuid().ToString("D"), info.Name, fullPath);
            folders.Add(folder);
            Save(folders);
            return folder;
        }
    }

    public bool Remove(string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        lock (sync)
        {
            var folders = Load();
            if (folders.RemoveAll(item => string.Equals(item.Id, folderId, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            Save(folders);
            return true;
        }
    }

    public IReadOnlyList<SharedDocumentEntry> List(string? parentDocumentId)
    {
        lock (sync)
        {
            var folders = Load();
            if (string.IsNullOrWhiteSpace(parentDocumentId))
            {
                return folders
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(item => new SharedDocumentEntry(
                        EncodeToken(item.Id, string.Empty),
                        item.DisplayName,
                        "inode/directory",
                        0,
                        IsDirectory: true))
                    .ToArray();
            }

            var resolved = ResolveToken(parentDocumentId, folders, requireFile: false);
            if (!Directory.Exists(resolved.FullPath))
            {
                throw new DirectoryNotFoundException("A pasta compartilhada não está mais disponível.");
            }

            return new DirectoryInfo(resolved.FullPath)
                .EnumerateFileSystemInfos()
                .Where(item => !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(item => item is DirectoryInfo)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(500)
                .Select(item =>
                {
                    var relativePath = Path.GetRelativePath(resolved.Root.Path, item.FullName);
                    var isDirectory = item is DirectoryInfo;
                    return new SharedDocumentEntry(
                        EncodeToken(resolved.Root.Id, relativePath),
                        item.Name,
                        isDirectory ? "inode/directory" : "application/octet-stream",
                        item is FileInfo file ? file.Length : 0,
                        isDirectory);
                })
                .ToArray();
        }
    }

    public string ResolveFile(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        lock (sync)
        {
            return ResolveToken(documentId, Load(), requireFile: true).FullPath;
        }
    }

    private ResolvedDocument ResolveToken(
        string token,
        IReadOnlyCollection<SharedFolder> folders,
        bool requireFile)
    {
        SharedDocumentToken document;
        byte[] plaintext;
        try
        {
            plaintext = protector.Unprotect(FromBase64Url(token));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException("O identificador de documento é inválido.", exception);
        }

        try
        {
            document = JsonSerializer.Deserialize<SharedDocumentToken>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("O identificador de documento está vazio.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var root = folders.SingleOrDefault(item =>
            string.Equals(item.Id, document.RootId, StringComparison.Ordinal))
            ?? throw new UnauthorizedAccessException("A raiz compartilhada foi removida.");
        var fullPath = Path.GetFullPath(Path.Combine(root.Path, document.RelativePath ?? string.Empty));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root.Path);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("O documento está fora da pasta compartilhada.");
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            (requireFile && attributes.HasFlag(FileAttributes.Directory)))
        {
            throw new UnauthorizedAccessException("O documento solicitado não pode ser compartilhado.");
        }

        return new ResolvedDocument(root, fullPath);
    }

    private string EncodeToken(string rootId, string relativePath)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new SharedDocumentToken(rootId, relativePath),
            JsonOptions);
        try
        {
            return ToBase64Url(protector.Protect(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private List<SharedFolder> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var plaintext = protector.Unprotect(File.ReadAllBytes(filePath));
        try
        {
            var folders = JsonSerializer.Deserialize<List<SharedFolder>>(plaintext, JsonOptions) ?? [];
            if (folders.Any(item =>
                    !Guid.TryParseExact(item.Id, "D", out _) ||
                    string.IsNullOrWhiteSpace(item.DisplayName) ||
                    !Path.IsPathFullyQualified(item.Path)) ||
                folders.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != folders.Count)
            {
                throw new InvalidDataException("The shared folder store is invalid.");
            }

            return folders;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save(List<SharedFolder> folders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(folders, JsonOptions);
        try
        {
            var temporaryFile = filePath + ".tmp";
            File.WriteAllBytes(temporaryFile, protector.Protect(plaintext));
            File.Move(temporaryFile, filePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record SharedDocumentToken(string RootId, string RelativePath);

    private sealed record ResolvedDocument(SharedFolder Root, string FullPath);
}

public sealed record SharedFolder(string Id, string DisplayName, string Path);

public sealed record SharedDocumentEntry(
    string DocumentId,
    string DisplayName,
    string MimeType,
    long SizeBytes,
    bool IsDirectory);
