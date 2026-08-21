using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;
using Windows.Devices.WiFiDirect;

namespace Veyro.Desktop;

public sealed record WindowsTransportCapabilities(
    bool BluetoothLowEnergyApiAvailable,
    bool WiFiDirectApiAvailable,
    bool BluetoothAdapterAvailable,
    bool BluetoothRadioOn,
    bool BluetoothPeripheralRoleSupported)
{
    public bool BluetoothOperational =>
        BluetoothLowEnergyApiAvailable &&
        BluetoothAdapterAvailable &&
        BluetoothRadioOn;
}

public static class WindowsTransportCapabilityProbe
{
    public static async Task<WindowsTransportCapabilities> ProbeAsync()
    {
        var bleAvailable = TryProbeBle();
        var wifiDirectAvailable = TryProbeWifiDirect();
        var adapter = bleAvailable ? await BluetoothAdapter.GetDefaultAsync() : null;
        var radio = adapter is null ? null : await adapter.GetRadioAsync();
        return new WindowsTransportCapabilities(
            bleAvailable,
            wifiDirectAvailable,
            adapter is not null,
            radio?.State == RadioState.On,
            adapter?.IsPeripheralRoleSupported == true);
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
