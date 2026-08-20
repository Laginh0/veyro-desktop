namespace Veyro.Desktop.Core.Identity;

public sealed record LocalIdentity(
    string DeviceId,
    string DisplayName,
    long CreatedAtUnixMilliseconds);
