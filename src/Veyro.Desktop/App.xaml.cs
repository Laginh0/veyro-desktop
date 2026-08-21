using System.Windows;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Logging;
using Veyro.Desktop.Bluetooth;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.Pairing;
using Veyro.Desktop.FastChannel;
using Veyro.Desktop.Features;
using Veyro.Desktop.Core.Features;
using Veyro.Desktop.Core.Transport;
using Veyro.Desktop.WifiDirect;
using Application = System.Windows.Application;

namespace Veyro.Desktop;

public partial class App : Application
{
    private SanitizedFileLogger? logger;
    private TrayIconService? trayIcon;
    private BleDiscoveryService? bleDiscovery;
    private BlePairingCoordinator? pairingCoordinator;
    private FastChannelCoordinator? fastChannelCoordinator;
    private VeyroFeatureService? featureService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        logger = new SanitizedFileLogger(paths.LogDirectory);

        try
        {
            var protector = new DpapiIdentityProtector();
            var identityStore = new LocalIdentityStore(paths.IdentityFile, protector);
            var identity = identityStore.LoadOrCreate();
            var identityKey = new LocalIdentityKeyStore(paths.IdentityKeyFile, protector).LoadOrCreate();
            var trustStore = new TrustStore(paths.TrustFile, protector);
            var capabilities = await WindowsTransportCapabilityProbe.ProbeAsync();
            var advertisedCapabilities = VeyroCapability.BleControl |
                VeyroCapability.FileTransfer |
                VeyroCapability.Clipboard |
                VeyroCapability.Links |
                VeyroCapability.BatteryStatus |
                VeyroCapability.Ping;
            if (capabilities.WiFiDirectApiAvailable)
            {
                advertisedCapabilities |=
                    VeyroCapability.WifiDirectData | VeyroCapability.MultiDeviceRouting;
            }

            bleDiscovery = new BleDiscoveryService(advertisedCapabilities);
            pairingCoordinator = new BlePairingCoordinator(
                identity,
                identityKey,
                advertisedCapabilities,
                trustStore);
            var wifiDirectManager = new WifiDirectManager();
            var resumeRegistry = new FastChannelResumeRegistry(
                TimeSpan.FromHours(24),
                paths.ResumeSessionsFile,
                protector);
            fastChannelCoordinator = new FastChannelCoordinator(
                identity,
                identityKey,
                trustStore,
                pairingCoordinator,
                wifiDirectManager,
                advertisedCapabilities,
                resumeRegistry);
            var featurePermissions = new FeaturePermissionStore(paths.FeaturePermissionsFile, protector);
            var sharedFolders = new SharedFolderStore(paths.SharedFoldersFile, protector);
            featureService = new VeyroFeatureService(
                fastChannelCoordinator,
                trustStore,
                featurePermissions,
                sharedFolders,
                paths.IncomingFilesDirectory);

            var window = new MainWindow(
                identity,
                capabilities,
                paths,
                bleDiscovery,
                pairingCoordinator,
                trustStore,
                fastChannelCoordinator,
                featureService);
            trayIcon = new TrayIconService(window, Shutdown);
            featureService.NotificationReceived += FeatureService_NotificationReceived;
            Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                window.Hide();
            }

            if (capabilities.BluetoothOperational)
            {
                try
                {
                    await pairingCoordinator.StartAsync();
                }
                catch (Exception exception)
                {
                    window.ReportBluetoothFailure(exception.Message);
                    logger.Write(
                        LogLevel.Error,
                        "ble_gatt_start_failed",
                        new Dictionary<string, object?>
                        {
                            ["exception_type"] = exception.GetType().Name,
                            ["hresult"] = exception.HResult,
                            ["diagnostic"] = exception.Message
                        });
                }

                try
                {
                    bleDiscovery.Start();
                }
                catch (Exception exception)
                {
                    window.ReportBluetoothFailure(exception.Message);
                    logger.Write(
                        LogLevel.Error,
                        "ble_discovery_start_failed",
                        new Dictionary<string, object?>
                        {
                            ["exception_type"] = exception.GetType().Name,
                            ["hresult"] = exception.HResult,
                            ["diagnostic"] = exception.Message
                        });
                }
            }
            else
            {
                window.ReportBluetoothFailure(
                    capabilities.BluetoothAdapterAvailable
                        ? "Ligue o rádio Bluetooth e reinicie o Veyro para anunciar e descobrir dispositivos."
                        : "Nenhum adaptador Bluetooth LE foi encontrado.");
            }

            if (capabilities.WiFiDirectApiAvailable)
            {
                try
                {
                    fastChannelCoordinator.Start();
                }
                catch (Exception exception)
                {
                    window.ReportWifiDirectFailure(exception.Message);
                    logger.Write(
                        LogLevel.Error,
                        "wifi_direct_milestone_3_start_failed",
                        new Dictionary<string, object?> { ["exception_type"] = exception.GetType().Name });
                }
            }

            logger.Write(
                LogLevel.Information,
                "desktop_started",
                new Dictionary<string, object?>
                {
                    ["device_id"] = identity.DeviceId,
                    ["ble_api_available"] = capabilities.BluetoothLowEnergyApiAvailable,
                    ["ble_adapter_available"] = capabilities.BluetoothAdapterAvailable,
                    ["ble_radio_on"] = capabilities.BluetoothRadioOn,
                    ["ble_peripheral_role_supported"] = capabilities.BluetoothPeripheralRoleSupported,
                    ["wifi_direct_api_available"] = capabilities.WiFiDirectApiAvailable
                });
        }
        catch (Exception exception)
        {
            logger.Write(
                LogLevel.Error,
                "desktop_start_failed",
                new Dictionary<string, object?> { ["exception_type"] = exception.GetType().Name });
            System.Windows.MessageBox.Show(
                "Não foi possível iniciar o Veyro. Consulte os logs locais para obter o diagnóstico.",
                "Veyro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        if (featureService is not null)
        {
            featureService.NotificationReceived -= FeatureService_NotificationReceived;
            featureService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        trayIcon?.Dispose();
        bleDiscovery?.Dispose();
        fastChannelCoordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        pairingCoordinator?.Dispose();
        logger?.Write(LogLevel.Information, "desktop_stopped");
        logger?.Dispose();
        base.OnExit(e);
    }

    private void FeatureService_NotificationReceived(object? sender, VeyroNotificationEventArgs e) =>
        Dispatcher.Invoke(() =>
            trayIcon?.ShowNotification(
                string.IsNullOrWhiteSpace(e.AppName) ? e.Title : $"{e.AppName} · {e.Title}",
                e.Body));

    private void SystemEvents_PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume && fastChannelCoordinator is not null)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    fastChannelCoordinator.RecoverAfterSystemResume();
                }
                catch (Exception exception)
                {
                    logger?.Write(
                        LogLevel.Warning,
                        "resume_recovery_failed",
                        new Dictionary<string, object?> { ["exception_type"] = exception.GetType().Name });
                }
            });
        }
    }
}
