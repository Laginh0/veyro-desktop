namespace Veyro.Desktop.Core.Protocol;

public sealed record EnvelopeValidationResult(bool IsValid, string? ErrorCode)
{
    public static EnvelopeValidationResult Valid { get; } = new(true, null);

    public static EnvelopeValidationResult Invalid(string errorCode) => new(false, errorCode);
}
