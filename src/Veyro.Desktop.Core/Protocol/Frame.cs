namespace Veyro.Desktop.Core.Protocol;

public sealed record Frame(byte Flags, ReadOnlyMemory<byte> Payload);
