namespace Veyro.Desktop.Bluetooth;

public sealed record BleDiscoveryStatus(
    bool IsRunning,
    string Message,
    Exception? Error = null);
