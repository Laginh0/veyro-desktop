using System.Windows;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Logging;
using Veyro.Desktop.Bluetooth;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.Pairing;
using Application = System.Windows.Application;

namespace Veyro.Desktop;

public partial class App : Application
{
    private SanitizedFileLogger? logger;
    private TrayIconService? trayIcon;
    private BleDiscoveryService? bleDiscovery;
    private BlePairingCoordinator? pairingCoordinator;

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
            var capabilities = WindowsTransportCapabilityProbe.Probe();
            var advertisedCapabilities = VeyroCapability.BleControl;

            bleDiscovery = new BleDiscoveryService(advertisedCapabilities);
            pairingCoordinator = new BlePairingCoordinator(
                identity,
                identityKey,
                advertisedCapabilities,
                trustStore);

            var window = new MainWindow(
                identity,
                capabilities,
                paths,
                bleDiscovery,
                pairingCoordinator,
                trustStore);
            trayIcon = new TrayIconService(window, Shutdown);
            MainWindow = window;
            window.Show();

            if (capabilities.BluetoothLowEnergyApiAvailable)
            {
                try
                {
                    await pairingCoordinator.StartAsync();
                    bleDiscovery.Start();
                }
                catch (Exception exception)
                {
                    window.ReportBluetoothFailure(exception.Message);
                    logger.Write(
                        LogLevel.Error,
                        "ble_milestone_2_start_failed",
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
        trayIcon?.Dispose();
        bleDiscovery?.Dispose();
        pairingCoordinator?.Dispose();
        logger?.Write(LogLevel.Information, "desktop_stopped");
        logger?.Dispose();
        base.OnExit(e);
    }
}
