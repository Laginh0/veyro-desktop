namespace Veyro.Desktop.Bluetooth;

public sealed class BleControlPacketEventArgs(byte[] packet) : EventArgs
{
    public byte[] Packet { get; } = packet;
}
