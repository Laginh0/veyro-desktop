using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.WiFiDirect;

namespace Veyro.Desktop;

public sealed record WindowsTransportCapabilities(
    bool BluetoothLowEnergyApiAvailable,
    bool WiFiDirectApiAvailable);

public static class WindowsTransportCapabilityProbe
{
    public static WindowsTransportCapabilities Probe()
    {
        var bleAvailable = TryProbeBle();
        var wifiDirectAvailable = TryProbeWifiDirect();
        return new WindowsTransportCapabilities(bleAvailable, wifiDirectAvailable);
    }

    private static bool TryProbeBle()
    {
        try
        {
            _ = new BluetoothLEAdvertisementWatcher();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryProbeWifiDirect()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(
                WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint));
        }
        catch
        {
            return false;
        }
    }
}
