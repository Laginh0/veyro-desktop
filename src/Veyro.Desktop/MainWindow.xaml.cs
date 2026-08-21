using System.Windows;
using System.Windows.Media;
using Veyro.Desktop.Core;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Bluetooth;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Groups;
using Veyro.Desktop.Core.Features;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.Pairing;
using Veyro.Desktop.FastChannel;
using Veyro.Desktop.Features;
using Veyro.Protocol;
using MediaColor = System.Windows.Media.Color;

namespace Veyro.Desktop;

public partial class MainWindow : Window
{
    private readonly BleDiscoveryService discoveryService;
    private readonly BlePairingCoordinator pairingCoordinator;
    private readonly TrustStore trustStore;
    private readonly FastChannelCoordinator fastChannelCoordinator;
    private readonly VeyroFeatureService featureService;
    private readonly string localDeviceId;
    private readonly Dictionary<string, System.Windows.Point> stylusPositions = new(StringComparer.Ordinal);
    private bool permissionSelectionUpdating;

    public MainWindow(
        LocalIdentity identity,
        WindowsTransportCapabilities capabilities,
        AppPaths paths,
        BleDiscoveryService discoveryService,
        BlePairingCoordinator pairingCoordinator,
        TrustStore trustStore,
        FastChannelCoordinator fastChannelCoordinator,
        VeyroFeatureService featureService)
    {
        InitializeComponent();
        this.discoveryService = discoveryService;
        this.pairingCoordinator = pairingCoordinator;
        this.trustStore = trustStore;
        this.fastChannelCoordinator = fastChannelCoordinator;
        this.featureService = featureService;
        localDeviceId = identity.DeviceId;

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
        fastChannelCoordinator.GroupStateChanged += FastChannelCoordinator_GroupStateChanged;
        featureService.StatusChanged += FeatureService_StatusChanged;
        featureService.AuthorizationRequested += FeatureService_AuthorizationRequested;
        featureService.ClipboardReceived += FeatureService_ClipboardReceived;
        featureService.RemoteDeviceStateChanged += FeatureService_RemoteDeviceStateChanged;
        featureService.RemoteStylusReceived += FeatureService_RemoteStylusReceived;
        featureService.RemoteFilesReceived += FeatureService_RemoteFilesReceived;
        PermissionFeatureCombo.ItemsSource = FeaturePermissionItem.All;
        PermissionPolicyCombo.ItemsSource = FeaturePolicyItem.All;
        SecureCommandCombo.ItemsSource = SafeCommandItem.All;
        PermissionFeatureCombo.SelectedIndex = 0;
        SecureCommandCombo.SelectedIndex = 0;
        RefreshNearbyDevices();
        RefreshTrustedDevices();
        RefreshSharedFolders();
        RefreshGroupState(
            fastChannelCoordinator.GroupEpoch,
            fastChannelCoordinator.CoordinatorDeviceId,
            fastChannelCoordinator.GroupMembers);
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

    private void FastChannelCoordinator_GroupStateChanged(object? sender, GroupStateChangedEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
            RefreshGroupState(e.Epoch, e.CoordinatorDeviceId, e.Members));

    private void RefreshGroupState(
        ulong epoch,
        string coordinatorDeviceId,
        IReadOnlyList<GroupMemberState> members)
    {
        var availableMembers = members.Count(member => member.IsAvailable);
        var coordinator = members.FirstOrDefault(member =>
            string.Equals(member.DeviceId, coordinatorDeviceId, StringComparison.Ordinal));
        var coordinatorName = string.Equals(
            coordinatorDeviceId,
            localDeviceId,
            StringComparison.Ordinal)
            ? "este computador"
            : coordinator?.DisplayName ?? coordinatorDeviceId[..Math.Min(8, coordinatorDeviceId.Length)];
        GroupStatusText.Text = $"Grupo: {availableMembers} membro(s) · coordenador: {coordinatorName} · época {epoch}";
        FeatureDestinationsList.ItemsSource = members
            .Where(member => member.IsAvailable &&
                !string.Equals(member.DeviceId, localDeviceId, StringComparison.Ordinal) &&
                trustStore.FindActive(member.DeviceId) is not null)
            .Select(member => new FeatureDestinationItem(member.DeviceId, member.DisplayName))
            .ToArray();
    }

    private void FeatureService_StatusChanged(object? sender, FeatureStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            FeatureStatusText.Text = e.Error is null ? e.Message : $"{e.Message}: {e.Error.Message}";
            FeatureStatusText.Foreground = new SolidColorBrush(
                e.Error is null ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
        });

    private void FeatureService_AuthorizationRequested(object? sender, FeatureAuthorizationEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var result = System.Windows.MessageBox.Show(
                $"{e.DeviceName} quer {e.Description}.\n\nPermitir esta ação?",
                "Permissão contextual Veyro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            e.Complete(result == MessageBoxResult.Yes);
        });

    private void FeatureService_ClipboardReceived(object? sender, ClipboardReceivedEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(e.Text);
                FeatureStatusText.Text = $"Clipboard recebido de {e.DeviceName}";
            }
            catch (Exception exception)
            {
                FeatureStatusText.Text = $"Não foi possível atualizar o clipboard: {exception.Message}";
            }
        });

    private void FeatureService_RemoteDeviceStateChanged(object? sender, RemoteDeviceStateEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var deviceName = trustStore.FindActive(e.DeviceId)?.DisplayName ?? e.DeviceId;
            if (e.Battery is not null)
            {
                RemoteStateText.Text = $"{deviceName}: bateria {e.Battery.ChargePercentage}%";
            }
            else if (e.Connectivity is not null)
            {
                RemoteStateText.Text = $"{deviceName}: conexão {e.Connectivity.ActiveTransport}";
            }
            else if (e.PingRoundTrip is not null)
            {
                RemoteStateText.Text = $"{deviceName}: ping {e.PingRoundTrip.Value.TotalMilliseconds:F0} ms";
            }
        });

    private void FeatureService_RemoteStylusReceived(object? sender, RemoteStylusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var input = e.Input;
            if (TabletCanvas.ActualWidth <= 0 || TabletCanvas.ActualHeight <= 0)
            {
                return;
            }

            var point = new System.Windows.Point(
                Math.Clamp(input.NormalizedX, 0, 1) * TabletCanvas.ActualWidth,
                Math.Clamp(input.NormalizedY, 0, 1) * TabletCanvas.ActualHeight);
            if (input.StylusAction == StylusAction.StylusDown)
            {
                stylusPositions[e.DeviceId] = point;
                return;
            }

            if (input.StylusAction == StylusAction.StylusMove &&
                stylusPositions.TryGetValue(e.DeviceId, out var previous))
            {
                TabletCanvas.Children.Add(
                    new System.Windows.Shapes.Line
                    {
                        X1 = previous.X,
                        Y1 = previous.Y,
                        X2 = point.X,
                        Y2 = point.Y,
                        Stroke = new SolidColorBrush(MediaColor.FromRgb(23, 58, 99)),
                        StrokeThickness = 1.5 + Math.Clamp(input.Pressure, 0, 1) * 10,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });
                stylusPositions[e.DeviceId] = point;
            }

            if (input.StylusAction is StylusAction.StylusUp or StylusAction.StylusCancel)
            {
                stylusPositions.Remove(e.DeviceId);
            }
        });

    private void FeatureService_RemoteFilesReceived(object? sender, RemoteFilesEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            RemoteFilesList.ItemsSource = e.Entries
                .Select(entry => new RemoteFileItem(e.DeviceId, entry))
                .ToArray();
            FeatureStatusText.Text = $"{e.Entries.Count} item(ns) recebidos";
        });

    private void PairingCoordinator_PinAvailable(object? sender, PairingPinEventArgs e) =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
#if DEBUG
            DiscoveryStatusText.Text = "DEBUG: PIN confirmado automaticamente";
            try
            {
                await pairingCoordinator.ConfirmPinAsync(true);
            }
            catch (Exception exception)
            {
                ReportBluetoothFailure(exception.Message);
            }
#else
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
#endif
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
            featureService.RemovePermissions(item.Device.DeviceId);
            fastChannelCoordinator.InvalidateResumeState(item.Device.DeviceId);
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
        var trustedItems = trustStore.Snapshot()
            .Where(device => !device.IsRevoked)
            .Select(device => new TrustedDeviceItem(device))
            .ToArray();
        TrustedDevicesList.ItemsSource = trustedItems;
        PermissionDeviceCombo.ItemsSource = trustedItems;
        if (trustedItems.Length > 0)
        {
            PermissionDeviceCombo.SelectedIndex = 0;
        }
    }

    private void RefreshSharedFolders() => SharedFoldersList.ItemsSource = featureService.SharedFolders;

    private string[] SelectedDestinationIds() => FeatureDestinationsList.SelectedItems
        .Cast<FeatureDestinationItem>()
        .Select(item => item.DeviceId)
        .ToArray();

    private string[] RequireDestinations()
    {
        var destinations = SelectedDestinationIds();
        if (destinations.Length == 0)
        {
            throw new InvalidOperationException("Selecione ao menos um destino conectado.");
        }

        return destinations;
    }

    private async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Selecionar arquivos para enviar"
            };
            if (dialog.ShowDialog(this) == true)
            {
                await featureService.SendFilesAsync(dialog.FileNames, RequireDestinations());
            }
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                throw new InvalidOperationException("O clipboard não contém texto.");
            }

            await featureService.SendClipboardAsync(
                System.Windows.Clipboard.GetText(),
                RequireDestinations());
            FeatureStatusText.Text = "Clipboard enviado";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await featureService.SendLinkAsync(LinkTextBox.Text, RequireDestinations());
            FeatureStatusText.Text = "Link enviado";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendDeviceState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var destinations = RequireDestinations();
            await featureService.SendBatteryStatusAsync(destinations);
            await featureService.SendConnectivityStatusAsync(destinations);
            FeatureStatusText.Text = "Estado do computador enviado";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var destination = FeatureDestinationsList.SelectedItems
                .Cast<FeatureDestinationItem>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Selecione um destino para o ping.");
            await featureService.PingAsync(destination.DeviceId);
            FeatureStatusText.Text = "Ping enviado";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SyncNotifications_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await featureService.SyncWindowsNotificationsAsync(RequireDestinations());
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = (sender as System.Windows.Controls.Button)?.CommandParameter as string;
            var category = command switch
            {
                "Play" => MediaEventCategory.CmdPlay,
                "Pause" => MediaEventCategory.CmdPause,
                "Next" => MediaEventCategory.CmdNext,
                _ => throw new InvalidOperationException("Comando de mídia inválido.")
            };
            await featureService.SendMediaCommandAsync(category, RequireDestinations());
            FeatureStatusText.Text = "Comando de mídia enviado";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendPresentation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = (sender as System.Windows.Controls.Button)?.CommandParameter as string;
            var action = command switch
            {
                "Start" => PresentationAction.PresentationStart,
                "Stop" => PresentationAction.PresentationStop,
                "Blackout" => PresentationAction.PresentationBlackoutOn,
                _ => throw new InvalidOperationException("Ação de apresentação inválida.")
            };
            await featureService.SendPresentationActionAsync(action, RequireDestinations());
            FeatureStatusText.Text = "Ação de apresentação enviada";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private async void SendSecureCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = SecureCommandCombo.SelectedItem as SafeCommandItem
                ?? throw new InvalidOperationException("Selecione uma ação segura.");
            await featureService.SendSafeCommandAsync(command.Command, RequireDestinations());
            FeatureStatusText.Text = "Ação segura enviada";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private void AddSharedFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Selecione uma pasta para compartilhar pelo Veyro",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            featureService.AddSharedFolder(dialog.SelectedPath);
            RefreshSharedFolders();
            FeatureStatusText.Text = "Pasta adicionada";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private void RemoveSharedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SharedFoldersList.SelectedItem is SharedFolder folder &&
            featureService.RemoveSharedFolder(folder.Id))
        {
            RefreshSharedFolders();
            FeatureStatusText.Text = "Pasta removida";
        }
    }

    private async void RequestRemoteFolders_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var destination = SelectedFeatureDestination();
            await featureService.RequestRemoteFilesAsync(destination.DeviceId);
            FeatureStatusText.Text = "Solicitação de pastas enviada";
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private void RemoteFilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenSelectedRemoteFile();

    private void OpenRemoteFile_Click(object sender, RoutedEventArgs e) => OpenSelectedRemoteFile();

    private async void OpenSelectedRemoteFile()
    {
        if (RemoteFilesList.SelectedItem is not RemoteFileItem item)
        {
            return;
        }

        try
        {
            if (item.Entry.IsDirectory)
            {
                await featureService.RequestRemoteFilesAsync(item.DeviceId, item.Entry.DocumentId);
            }
            else
            {
                await featureService.RequestRemoteFileDownloadAsync(item.DeviceId, item.Entry.DocumentId);
            }
        }
        catch (Exception exception)
        {
            FeatureStatusText.Text = exception.Message;
        }
    }

    private FeatureDestinationItem SelectedFeatureDestination() =>
        FeatureDestinationsList.SelectedItems.Cast<FeatureDestinationItem>().FirstOrDefault()
        ?? throw new InvalidOperationException("Selecione um destino conectado.");

    private void ClearTablet_Click(object sender, RoutedEventArgs e)
    {
        TabletCanvas.Children.Clear();
        stylusPositions.Clear();
    }

    private void PermissionDeviceCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => RefreshPermissionPolicy();

    private void PermissionFeatureCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => RefreshPermissionPolicy();

    private void RefreshPermissionPolicy()
    {
        if (PermissionDeviceCombo.SelectedItem is not TrustedDeviceItem device ||
            PermissionFeatureCombo.SelectedItem is not FeaturePermissionItem feature)
        {
            return;
        }

        permissionSelectionUpdating = true;
        try
        {
            var current = featureService.GetPolicy(device.Device.DeviceId, feature.Feature);
            PermissionPolicyCombo.SelectedItem = FeaturePolicyItem.All.Single(item => item.Policy == current);
        }
        finally
        {
            permissionSelectionUpdating = false;
        }
    }

    private void PermissionPolicyCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (permissionSelectionUpdating ||
            PermissionDeviceCombo.SelectedItem is not TrustedDeviceItem device ||
            PermissionFeatureCombo.SelectedItem is not FeaturePermissionItem feature ||
            PermissionPolicyCombo.SelectedItem is not FeaturePolicyItem policy)
        {
            return;
        }

        featureService.SetPolicy(device.Device.DeviceId, feature.Feature, policy.Policy);
        FeatureStatusText.Text = $"Permissão de {feature.DisplayName} atualizada para {device.DisplayName}";
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

public sealed record FeatureDestinationItem(string DeviceId, string DisplayName)
{
    public string Details => $"ID {DeviceId[..Math.Min(8, DeviceId.Length)]}";
}

public sealed record FeaturePermissionItem(VeyroFeature Feature, string DisplayName)
{
    public static IReadOnlyList<FeaturePermissionItem> All { get; } =
    [
        new(VeyroFeature.Files, "Arquivos"),
        new(VeyroFeature.Clipboard, "Clipboard"),
        new(VeyroFeature.Links, "Links"),
        new(VeyroFeature.Notifications, "Notificações"),
        new(VeyroFeature.MediaControl, "Controle de mídia"),
        new(VeyroFeature.SecureCommands, "Comandos seguros"),
        new(VeyroFeature.Presentation, "Apresentação"),
        new(VeyroFeature.RemoteInput, "Mouse, teclado e caneta"),
        new(VeyroFeature.SharedFolders, "Pastas compartilhadas")
    ];
}

public sealed record RemoteFileItem(string DeviceId, RemoteFileEntry Entry)
{
    public string DisplayName => Entry.IsDirectory ? $"📁 {Entry.DisplayName}" : Entry.DisplayName;

    public string Details => Entry.IsDirectory ? "Pasta" : $"{Entry.SizeBytes / 1024d:F1} KiB";
}

public sealed record FeaturePolicyItem(FeatureAccessPolicy Policy, string DisplayName)
{
    public static IReadOnlyList<FeaturePolicyItem> All { get; } =
    [
        new(FeatureAccessPolicy.Disabled, "Bloquear"),
        new(FeatureAccessPolicy.Ask, "Perguntar"),
        new(FeatureAccessPolicy.Allow, "Permitir")
    ];
}

public sealed record SafeCommandItem(string Command, string DisplayName)
{
    public static IReadOnlyList<SafeCommandItem> All { get; } =
    [
        new("lock-workstation", "Bloquear dispositivo"),
        new("ms-settings:bluetooth", "Abrir Bluetooth"),
        new("ms-settings:network-wifi", "Abrir Wi-Fi"),
        new("ms-settings:display", "Abrir tela")
    ];
}
