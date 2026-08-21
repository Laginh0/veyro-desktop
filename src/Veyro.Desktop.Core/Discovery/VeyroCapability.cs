namespace Veyro.Desktop.Core.Discovery;

[Flags]
public enum VeyroCapability : byte
{
    None = 0,
    BleControl = 1 << 0,
    WifiDirectData = 1 << 1,
    MultiDeviceRouting = 1 << 2,
    FileTransfer = 1 << 3,
    Clipboard = 1 << 4,
    Links = 1 << 5,
    BatteryStatus = 1 << 6,
    Ping = 1 << 7
}
