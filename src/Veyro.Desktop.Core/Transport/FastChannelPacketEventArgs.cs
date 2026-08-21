namespace Veyro.Desktop.Core.Transport;

public sealed class FastChannelPacketEventArgs(Veyro.Protocol.FastChannelPacket packet) : EventArgs
{
    public Veyro.Protocol.FastChannelPacket Packet { get; } = packet;
}
