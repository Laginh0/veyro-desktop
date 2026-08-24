using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
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
using Veyro.Desktop.Views;
using Veyro.Protocol;
using MediaColor = System.Windows.Media.Color;

namespace Veyro.Desktop;

public partial class MainWindow : Window
{
    private const string DebugPreviewDeviceId = "__veyro_debug_preview__";
    private readonly BleDiscoveryService discoveryService;
    private readonly BlePairingCoordinator pairingCoordinator;
    private readonly TrustStore trustStore;
    private readonly FastChannelCoordinator fastChannelCoordinator;
    private readonly VeyroFeatureService featureService;
    private readonly string localDeviceId;
    private readonly Dictionary<string, System.Windows.Point> stylusPositions = new(StringComparer.Ordinal);
    private readonly ObservableCollection<TransferActivityItem> transferActivities = [];
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool permissionSelectionUpdating;
    private bool automaticReconnectInProgress;
    private ulong lastAutomaticReconnectAddress;
    private DateTimeOffset lastAutomaticReconnectAt;
    private CancellationTokenSource? activeTransferCancellation;

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
        featureService.FileTransferProgressChanged += FeatureService_FileTransferProgressChanged;
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
        StartWithWindowsCheckBox.IsChecked = StartupRegistrationService.IsEnabled();
        toastTimer.Tick += (_, _) =>
        {
            toastTimer.Stop();
            ToastHost.Visibility = Visibility.Collapsed;
        };
        ApplyTheme("System");
        UpdateTransferLists();
        UpdateResponsiveLayout(ActualWidth);
#if DEBUG
        EnableDebugPreview();
#endif
    }

    public void ReportBluetoothFailure(string message)
    {
        DiscoveryStatusText.Text = $"Bluetooth indisponível: {message}";
        DiscoveryStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(154, 52, 52));
        GlobalConnectionText.Text = "Bluetooth indisponível";
        ShowToast($"Bluetooth indisponível. {message}", isError: true);
    }

    public void ReportWifiDirectFailure(string message)
    {
        WifiDirectStatusText.Text = $"●  Indisponível: {message}";
        WifiDirectStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(154, 52, 52));
        ShowToast($"Wi-Fi Direct indisponível. {message}", isError: true);
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
        _ = Dispatcher.InvokeAsync(async () =>
        {
            RefreshNearbyDevices();
            await TryAutomaticReconnectAsync();
        });

    private async Task TryAutomaticReconnectAsync()
    {
        if (automaticReconnectInProgress || pairingCoordinator.ActiveTrustedDeviceId is not null ||
            !trustStore.Snapshot().Any(device => !device.IsRevoked))
        {
            return;
        }

        var candidate = discoveryService.Devices.OrderByDescending(device => device.SignalStrengthDbm).FirstOrDefault();
        if (candidate is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (candidate.BluetoothAddress == lastAutomaticReconnectAddress &&
            now - lastAutomaticReconnectAt < TimeSpan.FromSeconds(20))
        {
            return;
        }

        automaticReconnectInProgress = true;
        lastAutomaticReconnectAddress = candidate.BluetoothAddress;
        lastAutomaticReconnectAt = now;
        try
        {
            await pairingCoordinator.BeginReconnectAsync(candidate);
        }
        catch
        {
            // Status is already surfaced by the pairing coordinator. A later
            // advertisement can retry without ever opening the PIN flow.
        }
        finally
        {
            automaticReconnectInProgress = false;
        }
    }

    private void DiscoveryService_StatusChanged(object? sender, BleDiscoveryStatus e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            DiscoveryStatusText.Text = e.Message;
            DiscoveryStatusText.Foreground = new SolidColorBrush(
                e.IsRunning ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
            GlobalConnectionText.Text = e.IsRunning ? "Procurando dispositivos próximos" : "Descoberta pausada";
        });

    private void PairingCoordinator_StatusChanged(object? sender, PairingStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            DiscoveryStatusText.Text = e.Error is null ? e.Message : $"{e.Message}: {e.Error.Message}";
            DiscoveryStatusText.Foreground = new SolidColorBrush(
                e.Error is null ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
            GlobalConnectionText.Text = e.Error is null ? e.Message : "A conexão precisa de atenção";
            ShowToast(e.Error is null ? e.Message : $"{e.Message}. {e.Error.Message}", e.Error is not null);
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
            GlobalConnectionText.Text = fastChannelCoordinator.ActiveSessionCount > 0
                ? ActiveSessionsText.Text
                : e.Message;
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
        var destinations = members
            .Where(member => member.IsAvailable &&
                !string.Equals(member.DeviceId, localDeviceId, StringComparison.Ordinal) &&
                trustStore.FindActive(member.DeviceId) is not null)
            .Select(member => new FeatureDestinationItem(member.DeviceId, member.DisplayName))
            .ToArray();
#if DEBUG
        if (destinations.Length == 0)
        {
            destinations =
            [
                new FeatureDestinationItem(
                    DebugPreviewDeviceId,
                    "Dispositivo de pré-visualização")
            ];
        }
#endif
        FeatureDestinationsList.ItemsSource = destinations;
#if DEBUG
        if (destinations.Length == 1 && destinations[0].DeviceId == DebugPreviewDeviceId)
        {
            FeatureDestinationsList.SelectedIndex = 0;
        }
#endif
        DestinationEmptyState.Visibility = destinations.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (destinations.Length == 1 && FeatureDestinationsList.SelectedIndex < 0)
        {
            FeatureDestinationsList.SelectedIndex = 0;
        }
    }

#if DEBUG
    private void EnableDebugPreview()
    {
        DebugPreviewBanner.Visibility = Visibility.Visible;
        FeatureStatusText.Text = "Modo de pré-visualização: recursos liberados para avaliação da interface";
        FeatureStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(118, 91, 0));
    }
#endif

    private void FeatureService_StatusChanged(object? sender, FeatureStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            FeatureStatusText.Text = e.Error is null ? e.Message : $"{e.Message}: {e.Error.Message}";
            FeatureStatusText.Foreground = new SolidColorBrush(
                e.Error is null ? MediaColor.FromRgb(54, 86, 117) : MediaColor.FromRgb(154, 52, 52));
            ShowToast(e.Error is null ? e.Message : $"{e.Message}. {e.Error.Message}", e.Error is not null);
        });

    private void FeatureService_FileTransferProgressChanged(object? sender, FileTransferProgressEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var key = $"{e.TransferId}:{e.DeviceId}";
            var item = transferActivities.FirstOrDefault(activity => activity.Key == key);
            if (item is null)
            {
                item = new TransferActivityItem(key);
                transferActivities.Insert(0, item);
            }

            item.Update(e);
            UpdateTransferLists();
            if (e.Stage == FileTransferStage.Completed)
            {
                ShowToast($"{e.FileName} chegou ao destino.");
            }
            else if (e.Stage == FileTransferStage.Failed)
            {
                ShowToast($"Não foi possível transferir {e.FileName}. {e.Error?.Message}", isError: true);
            }
        });

    private void FeatureService_AuthorizationRequested(object? sender, FeatureAuthorizationEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var accepted = DecisionDialog.Ask(
                this,
                "Solicitação recebida",
                $"{e.DeviceName} quer se conectar",
                $"O dispositivo quer {e.Description}.\n\nPermita apenas se você reconhece o dispositivo e esperava esta ação.",
                "Permitir uma vez",
                "Recusar",
                "\uE8A5");
            e.Complete(accepted);
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
            RemoteFilesEmptyState.Visibility = e.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FeatureStatusText.Text = $"{e.Entries.Count} item(ns) recebidos";
        });

    private void PairingCoordinator_PinAvailable(object? sender, PairingPinEventArgs e) =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var accepted = DecisionDialog.Ask(
                this,
                "Confirmar identidade",
                $"Compare com {e.Verification.RemoteDisplayName}",
                $"O mesmo código deve aparecer nos dois dispositivos:\n\n        {e.Verification.Pin}\n\nSe os códigos forem diferentes, recuse a conexão.",
                "Os códigos são iguais",
                "Recusar",
                "\uE73E");
            try
            {
                await pairingCoordinator.ConfirmPinAsync(accepted);
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

        var accepted = DecisionDialog.Ask(
            this,
            "Remover confiança",
            $"Esquecer {item.Device.DisplayName}?",
            "O dispositivo perderá acesso aos recursos permitidos. Para conectá-lo novamente, será necessário confirmar um novo PIN.",
            "Esquecer dispositivo",
            "Manter",
            "\uE74D");
        if (accepted)
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
        var devices = discoveryService.Devices
            .Select(device => new NearbyDeviceItem(device))
            .ToArray();
        NearbyDevicesList.ItemsSource = devices;
        NearbyEmptyState.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTrustedDevices()
    {
        var trustedItems = trustStore.Snapshot()
            .Where(device => !device.IsRevoked)
            .Select(device => new TrustedDeviceItem(device))
            .ToArray();
        TrustedDevicesList.ItemsSource = trustedItems;
        PermissionDeviceCombo.ItemsSource = trustedItems;
        TrustedEmptyState.Visibility = trustedItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PermissionDeviceCombo.IsEnabled = trustedItems.Length > 0;
        PermissionFeatureCombo.IsEnabled = trustedItems.Length > 0;
        PermissionPolicyCombo.IsEnabled = trustedItems.Length > 0;
        if (trustedItems.Length > 0)
        {
            PermissionDeviceCombo.SelectedIndex = 0;
        }
    }

    private void RefreshSharedFolders()
    {
        var folders = featureService.SharedFolders;
        SharedFoldersList.ItemsSource = folders;
        SharedFoldersEmptyState.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string[] SelectedDestinationIds() => FeatureDestinationsList.SelectedItems
        .Cast<FeatureDestinationItem>()
        .Select(item => item.DeviceId)
        .ToArray();

    private string[] RequireDestinations()
    {
        var destinations = SelectedDestinationIds();
#if DEBUG
        if (destinations.Contains(DebugPreviewDeviceId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Pré-visualização Debug: conecte um dispositivo real para enviar este comando.");
        }
#endif
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
                await SendFilePathsAsync(dialog.FileNames);
            }
        }
        catch (Exception exception)
        {
            ReportFeatureFailure(exception);
        }
    }

    private async Task SendFilePathsAsync(IReadOnlyCollection<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (activeTransferCancellation is not null)
        {
            throw new InvalidOperationException("Aguarde o envio atual terminar ou cancele-o na central de transferências.");
        }

        var transferCancellation = new CancellationTokenSource();
        activeTransferCancellation = transferCancellation;
        CancelTransfersButton.IsEnabled = true;
        HeroSendButton.IsEnabled = false;
        try
        {
            var destinations = RequireDestinations();
            ShowToast(filePaths.Count == 1
                ? "Preparando arquivo para envio direto…"
                : $"Preparando {filePaths.Count} arquivos para envio direto…");
            await featureService.SendFilesAsync(filePaths, destinations, transferCancellation.Token);
        }
        finally
        {
            CancelTransfersButton.IsEnabled = false;
            HeroSendButton.IsEnabled = true;
            if (ReferenceEquals(activeTransferCancellation, transferCancellation))
            {
                activeTransferCancellation = null;
            }

            transferCancellation.Dispose();
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
#if DEBUG
            if (destination.DeviceId == DebugPreviewDeviceId)
            {
                throw new InvalidOperationException(
                    "Pré-visualização Debug: conecte um dispositivo real para enviar o ping.");
            }
#endif
            await featureService.PingAsync(destination.DeviceId);
            FeatureStatusText.Text = "Ping enviado";
        }
        catch (Exception exception)
        {
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
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
            ReportFeatureFailure(exception);
        }
    }

    private FeatureDestinationItem SelectedFeatureDestination()
    {
        var destination = FeatureDestinationsList.SelectedItems
            .Cast<FeatureDestinationItem>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Selecione um destino conectado.");
#if DEBUG
        if (destination.DeviceId == DebugPreviewDeviceId)
        {
            throw new InvalidOperationException(
                "Pré-visualização Debug: conecte um dispositivo real para acessar seus arquivos.");
        }
#endif
        return destination;
    }

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
        ShowToast($"Permissão de {feature.DisplayName} atualizada para {device.DisplayName}.");
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (NearbyPage is null || sender is not System.Windows.Controls.RadioButton navigation)
        {
            return;
        }

        NearbyPage.Visibility = navigation == NearbyNavigation ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = navigation == TransfersNavigation ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = navigation == DevicesNavigation ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = navigation == SettingsNavigation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenTransfers_Click(object sender, RoutedEventArgs e) => TransfersNavigation.IsChecked = true;

    private void OpenNearby_Click(object sender, RoutedEventArgs e) => NearbyNavigation.IsChecked = true;

    private void SearchAgain_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            discoveryService.Start();
            RefreshNearbyDevices();
            ShowToast("Procurando novamente por dispositivos próximos…");
        }
        catch (Exception exception)
        {
            ReportBluetoothFailure(exception.Message);
        }
    }

    private void CancelTransfers_Click(object sender, RoutedEventArgs e)
    {
        activeTransferCancellation?.Cancel();
        CancelTransfersButton.IsEnabled = false;
        ShowToast("Cancelando o envio com segurança…");
    }

    private void ClearTransferHistory_Click(object sender, RoutedEventArgs e)
    {
        for (var index = transferActivities.Count - 1; index >= 0; index--)
        {
            if (!transferActivities[index].IsActive)
            {
                transferActivities.RemoveAt(index);
            }
        }

        UpdateTransferLists();
    }

    private void UpdateTransferLists()
    {
        var active = transferActivities.Where(item => item.IsActive).ToArray();
        var completed = transferActivities.Where(item => item.Stage == FileTransferStage.Completed).ToArray();
        var failed = transferActivities.Where(item =>
            item.Stage is FileTransferStage.Failed or FileTransferStage.Cancelled).ToArray();
        ActiveTransfersList.ItemsSource = active;
        CompletedTransfersList.ItemsSource = completed;
        FailedTransfersList.ItemsSource = failed;
        RecentTransfersList.ItemsSource = transferActivities.Take(2).ToArray();
        var hasTransfers = transferActivities.Count > 0;
        TransferEmptyState.Visibility = hasTransfers ? Visibility.Collapsed : Visibility.Visible;
        TransferContent.Visibility = hasTransfers ? Visibility.Visible : Visibility.Collapsed;
        RecentTransfersEmpty.Visibility = hasTransfers ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Window_PreviewDragEnter(object sender, System.Windows.DragEventArgs e) => UpdateDragFeedback(e);

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e) => UpdateDragFeedback(e);

    private void UpdateDragFeedback(System.Windows.DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
        e.Effects = hasFiles ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
        DragOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        if (hasFiles)
        {
            var selected = FeatureDestinationsList.SelectedItems.Cast<FeatureDestinationItem>().ToArray();
            DragTargetText.Text = selected.Length switch
            {
                0 => "selecione primeiro um dispositivo de destino",
                1 => $"para {selected[0].DisplayName}",
                _ => $"para {selected.Length} dispositivos"
            };
        }
    }

    private void Window_PreviewDragLeave(object sender, System.Windows.DragEventArgs e) =>
        DragOverlay.Visibility = Visibility.Collapsed;

    private async void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var files = paths.Where(File.Exists).ToArray();
        if (files.Length != paths.Length)
        {
            ShowToast("O envio de pastas ainda não está disponível. Selecione apenas arquivos.", isError: true);
        }

        try
        {
            await SendFilePathsAsync(files);
        }
        catch (Exception exception)
        {
            ShowToast(exception is OperationCanceledException
                ? "O envio foi cancelado."
                : exception.Message, exception is not OperationCanceledException);
        }
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistrationService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            ShowToast(StartWithWindowsCheckBox.IsChecked == true
                ? "O Veyro iniciará com o Windows."
                : "Inicialização automática desativada.");
        }
        catch (Exception exception)
        {
            StartWithWindowsCheckBox.IsChecked = StartupRegistrationService.IsEnabled();
            ShowToast($"Não foi possível alterar a inicialização. {exception.Message}", isError: true);
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            ApplyTheme(theme);
        }
    }

    private void ApplyTheme(string theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.Ordinal) ||
            string.Equals(theme, "System", StringComparison.Ordinal) && IsWindowsDarkTheme();
        var palette = dark
            ? new Dictionary<string, string>
            {
                ["ColorCanvas"] = "#0C121D", ["ColorSurface"] = "#121B29",
                ["ColorSurfaceRaised"] = "#172232", ["ColorSurfaceMuted"] = "#182332",
                ["ColorTextPrimary"] = "#F1F5FA", ["ColorTextSecondary"] = "#B6C0D0",
                ["ColorTextTertiary"] = "#8996AA", ["ColorBorder"] = "#263347",
                ["ColorBorderStrong"] = "#35445B", ["ColorPrimarySoft"] = "#1D315E",
                ["ColorConnectedSoft"] = "#15382D", ["ColorWarningSoft"] = "#3D3017",
                ["ColorErrorSoft"] = "#3D2025"
            }
            : new Dictionary<string, string>
            {
                ["ColorCanvas"] = "#F4F6FA", ["ColorSurface"] = "#FFFFFF",
                ["ColorSurfaceRaised"] = "#FFFFFF", ["ColorSurfaceMuted"] = "#F7F9FC",
                ["ColorTextPrimary"] = "#142033", ["ColorTextSecondary"] = "#596579",
                ["ColorTextTertiary"] = "#8791A2", ["ColorBorder"] = "#E3E8F0",
                ["ColorBorderStrong"] = "#CBD3DF", ["ColorPrimarySoft"] = "#EAF0FF",
                ["ColorConnectedSoft"] = "#E6F6EF", ["ColorWarningSoft"] = "#FFF4DA",
                ["ColorErrorSoft"] = "#FDECEC"
            };
        foreach (var (key, value) in palette)
        {
            System.Windows.Application.Current.Resources[key] =
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
        }
    }

    private static bool IsWindowsDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(e.NewSize.Width);

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D1: NearbyNavigation.IsChecked = true; e.Handled = true; break;
            case Key.D2: TransfersNavigation.IsChecked = true; e.Handled = true; break;
            case Key.D3: DevicesNavigation.IsChecked = true; e.Handled = true; break;
            case Key.D4: SettingsNavigation.IsChecked = true; e.Handled = true; break;
            case Key.O:
                NearbyNavigation.IsChecked = true;
                SendFiles_Click(HeroSendButton, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            FlowParticle.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(800))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            RadarPulse.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(1200))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
#if DEBUG
        var capturePath = Environment.GetEnvironmentVariable("VEYRO_DEBUG_CAPTURE_PATH");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            switch (Environment.GetEnvironmentVariable("VEYRO_DEBUG_CAPTURE_PAGE"))
            {
                case "Transfers": TransfersNavigation.IsChecked = true; break;
                case "Devices": DevicesNavigation.IsChecked = true; break;
                case "Settings": SettingsNavigation.IsChecked = true; break;
            }
            _ = Dispatcher.InvokeAsync(() => CaptureDebugPreview(capturePath), DispatcherPriority.ApplicationIdle);
        }
#endif
    }

#if DEBUG
    private void CaptureDebugPreview(string capturePath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(capturePath);
        encoder.Save(stream);
    }
#endif

    private void UpdateResponsiveLayout(double width)
    {
        if (SidebarColumn is null)
        {
            return;
        }

        var compact = width < 960;
        SidebarColumn.Width = new GridLength(compact ? 76 : 220);
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavigationLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarStatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HomeSideColumn.Width = new GridLength(width < 1100 ? 300 : 340);
        SettingsContent.Width = Math.Max(430, Math.Min(780, width - SidebarColumn.Width.Value - 68));
    }

    private void ShowToast(string message, bool isError = false)
    {
        if (ToastHost is null)
        {
            return;
        }

        ToastText.Text = message;
        ToastIcon.Text = isError ? "\uEA39" : "\uE73E";
        ToastHost.Background = new SolidColorBrush(isError
            ? MediaColor.FromRgb(113, 37, 45)
            : MediaColor.FromRgb(23, 34, 51));
        ToastHost.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void ReportFeatureFailure(Exception exception)
    {
        var cancelled = exception is OperationCanceledException;
        var message = cancelled ? "A ação foi cancelada." : exception.Message;
        FeatureStatusText.Text = message;
        FeatureStatusText.Foreground = cancelled
            ? (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
            : (System.Windows.Media.Brush)FindResource("ErrorBrush");
        ShowToast(message, isError: !cancelled);
    }
}

public sealed class TransferActivityItem(string key) : INotifyPropertyChanged
{
    private string fileName = "Arquivo";
    private string routeText = "Preparando conexão direta";
    private string statusText = "Preparando";
    private string detailText = "Calculando integridade…";
    private double progress;
    private FileTransferStage stage = FileTransferStage.Preparing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;
    public string FileName { get => fileName; private set => Set(ref fileName, value); }
    public string RouteText { get => routeText; private set => Set(ref routeText, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string DetailText { get => detailText; private set => Set(ref detailText, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string PercentText => Stage == FileTransferStage.AwaitingAcceptance
        ? "aguardando"
        : $"{Progress:F0}%";
    public FileTransferStage Stage
    {
        get => stage;
        private set
        {
            if (Set(ref stage, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }
    public bool IsActive => Stage is FileTransferStage.Preparing or
        FileTransferStage.AwaitingAcceptance or FileTransferStage.Transferring;

    public void Update(FileTransferProgressEventArgs update)
    {
        FileName = update.FileName;
        RouteText = update.Direction == FileTransferDirection.Sending
            ? $"Este computador  →  {update.DeviceName}"
            : $"{update.DeviceName}  →  Este computador";
        Stage = update.Stage;
        StatusText = update.Stage switch
        {
            FileTransferStage.Preparing => "Preparando",
            FileTransferStage.AwaitingAcceptance => "Aguardando aceite",
            FileTransferStage.Transferring => update.Direction == FileTransferDirection.Sending ? "Enviando" : "Recebendo",
            FileTransferStage.Completed => "Concluído",
            FileTransferStage.Failed => "Falhou",
            FileTransferStage.Cancelled => "Cancelado",
            _ => "Em andamento"
        };
        Progress = update.TotalBytes <= 0
            ? 0
            : Math.Clamp(update.TransferredBytes * 100d / update.TotalBytes, 0, 100);
        OnPropertyChanged(nameof(PercentText));

        var transferred = FormatBytes(update.TransferredBytes);
        var total = FormatBytes(update.TotalBytes);
        if (update.Stage == FileTransferStage.Completed)
        {
            DetailText = $"{total} · verificação de integridade concluída";
        }
        else if (update.Stage is FileTransferStage.Failed or FileTransferStage.Cancelled)
        {
            DetailText = update.Error?.Message ?? "A transferência foi interrompida.";
        }
        else if (update.TransferredBytes > 0 && update.Elapsed.TotalSeconds > 0.1)
        {
            var bytesPerSecond = update.TransferredBytes / update.Elapsed.TotalSeconds;
            var remaining = bytesPerSecond <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Max(0, update.TotalBytes - update.TransferredBytes) / bytesPerSecond);
            DetailText = $"{transferred} de {total} · {FormatBytes((long)bytesPerSecond)}/s · {FormatRemaining(remaining)}";
        }
        else
        {
            DetailText = update.Stage == FileTransferStage.AwaitingAcceptance
                ? $"{total} · esperando confirmação no outro dispositivo"
                : $"{transferred} de {total}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display.ToString(display >= 10 || unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalSeconds < 1
        ? "finalizando"
        : remaining.TotalMinutes < 1
            ? $"{Math.Ceiling(remaining.TotalSeconds):F0} s restantes"
            : $"{Math.Ceiling(remaining.TotalMinutes):F0} min restantes";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
