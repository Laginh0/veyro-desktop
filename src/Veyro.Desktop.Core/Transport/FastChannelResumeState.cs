namespace Veyro.Desktop.Core.Transport;

public sealed record FastChannelResumeState(
    string SessionId,
    string RemoteDeviceId,
    byte[] ResumeToken,
    ulong LastReceivedSequence,
    DateTimeOffset ExpiresAt);
