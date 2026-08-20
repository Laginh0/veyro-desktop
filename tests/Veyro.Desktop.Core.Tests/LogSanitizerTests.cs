using Veyro.Desktop.Core.Logging;

namespace Veyro.Desktop.Core.Tests;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData("clipboard_text")]
    [InlineData("sms_body")]
    [InlineData("private_key")]
    [InlineData("authentication_pin")]
    [InlineData("notification_content")]
    public void Sensitive_properties_are_redacted(string propertyName)
    {
        Assert.Equal("[redacted]", LogSanitizer.Property(propertyName, "private value"));
    }

    [Fact]
    public void Identifiers_are_pseudonymized()
    {
        var safeValue = LogSanitizer.Property("device_id", "abcdef12");

        Assert.StartsWith("sha256:", safeValue);
        Assert.DoesNotContain("abcdef12", safeValue);
    }

    [Fact]
    public void Newlines_cannot_inject_log_records()
    {
        Assert.Equal("ready forged", LogSanitizer.Property("state", "ready\nforged"));
    }
}
