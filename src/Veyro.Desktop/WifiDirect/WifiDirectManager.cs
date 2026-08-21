using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace Veyro.Desktop.WifiDirect;

public sealed class WifiDirectManager : IDisposable
{
    private readonly WiFiDirectAdvertisementPublisher publisher = new();
    private readonly WiFiDirectConnectionListener connectionListener = new();
    private readonly List<WiFiDirectDevice> connectedDevices = [];
    private bool disposed;

    public WifiDirectManager()
    {
        publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
        publisher.Advertisement.ListenStateDiscoverability =
            WiFiDirectAdvertisementListenStateDiscoverability.Normal;
        publisher.Advertisement.SupportedConfigurationMethods.Add(
            WiFiDirectConfigurationMethod.PushButton);
        publisher.StatusChanged += Publisher_StatusChanged;
        connectionListener.ConnectionRequested += ConnectionListener_ConnectionRequested;
    }

    public event EventHandler<WifiDirectStatusEventArgs>? StatusChanged;

    public event EventHandler<WifiDirectPeerConnection>? PeerConnected;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        publisher.Start();
        StatusChanged?.Invoke(this, new WifiDirectStatusEventArgs("Wi-Fi Direct aguardando um par Veyro"));
    }

    public async Task<IReadOnlyList<WifiDirectPeerCandidate>> DiscoverPeersAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices
            .Select(device => new WifiDirectPeerCandidate(device.Id, device.Name))
            .ToArray();
    }

    public async Task<WifiDirectPeerConnection> ConnectAsync(string deviceInformationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceInformationId);
        ObjectDisposedException.ThrowIf(disposed, this);
        var deviceInformation = await DeviceInformation.CreateFromIdAsync(deviceInformationId);
        var device = await WiFiDirectDevice.FromIdAsync(deviceInformationId)
            ?? throw new InvalidOperationException("O par Wi-Fi Direct não está mais disponível.");
        return RegisterConnectedDevice(deviceInformationId, deviceInformation.Name, device);
    }

    private async void ConnectionListener_ConnectionRequested(
        WiFiDirectConnectionListener sender,
        WiFiDirectConnectionRequestedEventArgs args)
    {
        try
        {
            using var request = args.GetConnectionRequest();
            var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
            if (device is null)
            {
                throw new InvalidOperationException("A solicitação Wi-Fi Direct expirou.");
            }

            _ = RegisterConnectedDevice(
                request.DeviceInformation.Id,
                request.DeviceInformation.Name,
                device);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new WifiDirectStatusEventArgs("Falha ao aceitar o par Wi-Fi Direct", exception));
        }
    }

    private WifiDirectPeerConnection RegisterConnectedDevice(
        string deviceInformationId,
        string displayName,
        WiFiDirectDevice device)
    {
        var endpoint = device.GetConnectionEndpointPairs().FirstOrDefault()
            ?? throw new InvalidOperationException("O enlace Wi-Fi Direct não forneceu endereços próprios.");
        connectedDevices.Add(device);
        var connection = new WifiDirectPeerConnection(
            deviceInformationId,
            displayName,
            endpoint.LocalHostName.RawName,
            endpoint.RemoteHostName.RawName);
        StatusChanged?.Invoke(this, new WifiDirectStatusEventArgs("Enlace Wi-Fi Direct formado"));
        PeerConnected?.Invoke(this, connection);
        return connection;
    }

    private void Publisher_StatusChanged(
        WiFiDirectAdvertisementPublisher sender,
        WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        var message = args.Status switch
        {
            WiFiDirectAdvertisementPublisherStatus.Started => "Grupo Wi-Fi Direct disponível",
            WiFiDirectAdvertisementPublisherStatus.Stopped => "Wi-Fi Direct interrompido",
            WiFiDirectAdvertisementPublisherStatus.Aborted => $"Wi-Fi Direct abortado: {args.Error}",
            _ => $"Wi-Fi Direct: {args.Status}"
        };
        StatusChanged?.Invoke(this, new WifiDirectStatusEventArgs(message));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        publisher.StatusChanged -= Publisher_StatusChanged;
        connectionListener.ConnectionRequested -= ConnectionListener_ConnectionRequested;
        if (publisher.Status is WiFiDirectAdvertisementPublisherStatus.Started or
            WiFiDirectAdvertisementPublisherStatus.Created)
        {
            publisher.Stop();
        }

        foreach (var device in connectedDevices)
        {
            device.Dispose();
        }

        connectedDevices.Clear();
    }
}
