using System.Windows;
using System.Windows.Media;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(
        LocalIdentity identity,
        WindowsTransportCapabilities capabilities,
        AppPaths paths)
    {
        InitializeComponent();

        DeviceNameText.Text = identity.DisplayName;
        DeviceIdText.Text = $"ID {identity.DeviceId}";
        DataPathText.Text = $"Dados locais: {paths.DataDirectory}";

        ApplyCapabilityStatus(
            BleStatusText,
            capabilities.BluetoothLowEnergyApiAvailable,
            "API disponível",
            "API indisponível");
        ApplyCapabilityStatus(
            WifiDirectStatusText,
            capabilities.WiFiDirectApiAvailable,
            "API disponível",
            "API indisponível");
    }

    private static void ApplyCapabilityStatus(
        System.Windows.Controls.TextBlock textBlock,
        bool available,
        string availableText,
        string unavailableText)
    {
        textBlock.Text = available ? $"●  {availableText}" : $"●  {unavailableText}";
        textBlock.Foreground = new SolidColorBrush(
            available
                ? System.Windows.Media.Color.FromRgb(36, 99, 59)
                : System.Windows.Media.Color.FromRgb(154, 52, 52));
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();
}
