using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Veyro.Desktop.Core.Logging;

public static partial class LogSanitizer
{
    private const string Redacted = "[redacted]";

    private static readonly string[] SensitiveFragments =
    [
        "auth", "clipboard", "contact", "content", "key", "message_body", "notification",
        "password", "payload", "phone", "pin", "private", "secret", "sms", "text", "token"
    ];

    public static string EventName(string value)
    {
        var normalized = SafeEventNameRegex().Replace(value.ToLowerInvariant(), "_").Trim('_');
        return string.IsNullOrEmpty(normalized) ? "invalid_event" : normalized[..Math.Min(64, normalized.Length)];
    }

    public static string Property(string name, object? value)
    {
        var normalizedName = name.ToLowerInvariant();
        if (SensitiveFragments.Any(normalizedName.Contains))
        {
            return Redacted;
        }

        if (value is null)
        {
            return "null";
        }

        if (normalizedName.EndsWith("_id", StringComparison.Ordinal))
        {
            return HashIdentifier(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        var rendered = value switch
        {
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            Enum enumValue => enumValue.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        rendered = rendered.Replace('\r', ' ').Replace('\n', ' ');
        return rendered[..Math.Min(160, rendered.Length)];
    }

    private static string HashIdentifier(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexStringLower(digest.AsSpan(0, 6))}";
    }

    [GeneratedRegex("[^a-z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafeEventNameRegex();
}
