using System.Windows;
using System.Windows.Media;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Bluetooth;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.Pairing;
using Veyro.Desktop.FastChannel;
using MediaColor = System.Windows.Media.Color;

namespace Veyro.Desktop;

public partial class MainWindow : Window
{
    private readonly BleDiscoveryService discoveryService;
    private readonly BlePairingCoordinator pairingCoordinator;
    private readonly TrustStore trustStore;
    private readonly FastChannelCoordinator fastChannelCoordinator;

    public MainWindow(
        LocalIdentity identity,
        WindowsTransportCapabilities capabilities,
        AppPaths paths,
        BleDiscoveryService discoveryService,
        BlePairingCoordinator pairingCoordinator,
        TrustStore trustStore,
        FastChannelCoordinator fastChannelCoordinator)
    {
        InitializeComponent();
        this.discoveryService = discoveryService;
        this.pairingCoordinator = pairingCoordinator;
        this.trustStore = trustStore;
        this.fastChannelCoordinator = fastChannelCoordinator;

        DeviceNameText.Text = identity.DisplayName;
        DeviceIdText.Text = $"ID {identity.DeviceId}";
        DataPathText.Text = $"Dados locais: {paths.DataDirectory}";

        ApplyCapabilityStatus(
            BleStatusText,
            capabilities.BluetoothOperational,
            "Rádio ligado",
            capabilities.BluetoothAdapterAvailable ? "Rádio desligado" : "Adaptador indisponível");
        ApplyCapabilityStatus(
            WifiDirectStatusText,
            capabilities.WiFiDirectApiAvailable,
            "API disponível",
            "API indisponível");

        discoveryService.DevicesChanged += DiscoveryService_DevicesChanged;
        discoveryService.StatusChanged += DiscoveryService_StatusChanged;
        pairingCoordinator.PinAvailable += PairingCoordinator_PinAvailable;
        pairingCoordinator.StatusChanged += PairingCoordinator_StatusChanged;
        pairingCoordinator.TrustChanged += PairingCoordinator_TrustChanged;
        fastChannelCoordinator.StatusChanged += FastChannelCoordinator_StatusChanged;
        RefreshNearbyDevices();
        RefreshTrustedDevices();
    }

    public void ReportBluetoothFailure(string message)
    {
        DiscoveryStatusText.Text = $"Bluetooth indisponível: {message}";
        DiscoveryStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(154, 52, 52));
    }

    public void ReportWifiDirectFailure(string message)
    {
        WifiDirectStatusText.Text = $"●  Indisponível: {message}";
        WifiDirectStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(154, 52, 52));
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

    private void DiscoveryService_DevicesChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(RefreshNearbyDevices);

    private void DiscoveryService_StatusChanged(object? sender, BleDiscoveryStatus e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            DiscoveryStatusText.Text = e.Message;
            DiscoveryStatusText.Foreground = new SolidColorBrush(
                e.IsRunning ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
        });

    private void PairingCoordinator_StatusChanged(object? sender, PairingStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            DiscoveryStatusText.Text = e.Error is null ? e.Message : $"{e.Message}: {e.Error.Message}";
            DiscoveryStatusText.Foreground = new SolidColorBrush(
                e.Error is null ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
        });

    private void PairingCoordinator_TrustChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(RefreshTrustedDevices);

    private void FastChannelCoordinator_StatusChanged(object? sender, FastChannelStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            WifiDirectStatusText.Text = $"●  {e.Message}";
            WifiDirectStatusText.Foreground = new SolidColorBrush(
                e.Error is null ? MediaColor.FromRgb(36, 99, 59) : MediaColor.FromRgb(154, 52, 52));
            ActiveSessionsText.Text = fastChannelCoordinator.ActiveSessionCount == 1
                ? "1 sessão segura"
                : $"{fastChannelCoordinator.ActiveSessionCount} sessões seguras";
        });

    private void PairingCoordinator_PinAvailable(object? sender, PairingPinEventArgs e) =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var result = System.Windows.MessageBox.Show(
                $"Confirme se o mesmo PIN aparece em {e.Verification.RemoteDisplayName}:\n\n{e.Verification.Pin}\n\nO PIN é igual nos dois dispositivos?",
                "Confirmar pareamento Veyro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            try
            {
                await pairingCoordinator.ConfirmPinAsync(result == MessageBoxResult.Yes);
            }
            catch (Exception exception)
            {
                ReportBluetoothFailure(exception.Message);
            }
        });

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (NearbyDevicesList.SelectedItem is not NearbyDeviceItem item)
        {
            return;
        }

        PairButton.IsEnabled = false;
        try
        {
            await pairingCoordinator.BeginPairingAsync(item.Device);
        }
        catch (Exception exception)
        {
            ReportBluetoothFailure(exception.Message);
        }
        finally
        {
            PairButton.IsEnabled = NearbyDevicesList.SelectedItem is not null;
        }
    }

    private void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrustedDevicesList.SelectedItem is not TrustedDeviceItem item)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Revogar a confiança em {item.Device.DisplayName}? Um novo pareamento com PIN será necessário.",
            "Revogar confiança",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            pairingCoordinator.Revoke(item.Device.DeviceId);
        }
    }

    private void NearbyDevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        PairButton.IsEnabled = NearbyDevicesList.SelectedItem is not null;

    private void TrustedDevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RevokeButton.IsEnabled = TrustedDevicesList.SelectedItem is not null;

    private void RefreshNearbyDevices()
    {
        NearbyDevicesList.ItemsSource = discoveryService.Devices
            .Select(device => new NearbyDeviceItem(device))
            .ToArray();
    }

    private void RefreshTrustedDevices()
    {
        TrustedDevicesList.ItemsSource = trustStore.Snapshot()
            .Where(device => !device.IsRevoked)
            .Select(device => new TrustedDeviceItem(device))
            .ToArray();
    }
}

public sealed record NearbyDeviceItem(DiscoveredDevice Device)
{
    public string DisplayName => $"Veyro próximo · {Device.EphemeralId[..6]}";

    public string Details => $"Sinal {Device.SignalStrengthDbm} dBm · protocolo {Device.ProtocolMajor}";
}

public sealed record TrustedDeviceItem(TrustedDevice Device)
{
    public string DisplayName => Device.DisplayName;

    public string Details => $"ID {Device.DeviceId} · confiável";
}
