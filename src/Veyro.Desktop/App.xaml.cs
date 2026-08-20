using System.Windows;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Logging;
using Application = System.Windows.Application;

namespace Veyro.Desktop;

public partial class App : Application
{
    private SanitizedFileLogger? logger;
    private TrayIconService? trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        logger = new SanitizedFileLogger(paths.LogDirectory);

        try
        {
            var identityStore = new LocalIdentityStore(paths.IdentityFile, new DpapiIdentityProtector());
            var identity = identityStore.LoadOrCreate();
            var capabilities = WindowsTransportCapabilityProbe.Probe();

            var window = new MainWindow(identity, capabilities, paths);
            trayIcon = new TrayIconService(window, Shutdown);
            MainWindow = window;
            window.Show();

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
        logger?.Write(LogLevel.Information, "desktop_stopped");
        logger?.Dispose();
        base.OnExit(e);
    }
}
