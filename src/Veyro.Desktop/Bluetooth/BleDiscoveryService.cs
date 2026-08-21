using System.Runtime.InteropServices.WindowsRuntime;
using Veyro.Desktop.Core.Discovery;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Veyro.Desktop.Bluetooth;

public sealed class BleDiscoveryService : IDisposable
{
    private readonly BluetoothLEAdvertisementPublisher publisher = new();
    private readonly BluetoothLEAdvertisementWatcher watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active
    };
    private readonly DiscoveredDeviceRegistry registry = new();
    private readonly System.Threading.Timer cleanupTimer;
    private readonly byte[] serviceUuidBytes = VeyroBluetoothProtocol.ServiceDataUuidBytes.ToArray();
    private readonly string localEphemeralId;
    private bool disposed;

    public BleDiscoveryService(VeyroCapability capabilities)
    {
        localEphemeralId = BleAdvertisementCodec.CreateEphemeralId();
        var advertisement = new VeyroBleAdvertisement(
            BleAdvertisementCodec.SupportedProtocolMajor,
            capabilities,
            localEphemeralId);

        byte[] serviceData = [.. serviceUuidBytes, .. BleAdvertisementCodec.Encode(advertisement)];
        publisher.Advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection
        {
            DataType = VeyroBluetoothProtocol.ServiceData128BitUuidType,
            Data = serviceData.AsBuffer()
        });

        watcher.Received += Watcher_Received;
        watcher.Stopped += Watcher_Stopped;
        publisher.StatusChanged += Publisher_StatusChanged;
        cleanupTimer = new System.Threading.Timer(RemoveExpiredDevices, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? DevicesChanged;

    public event EventHandler<BleDiscoveryStatus>? StatusChanged;

    public IReadOnlyList<DiscoveredDevice> Devices => registry.Snapshot();

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            publisher.Start();
            watcher.Start();
            cleanupTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            StatusChanged?.Invoke(this, new BleDiscoveryStatus(true, "Anunciando e procurando dispositivos próximos"));
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new BleDiscoveryStatus(false, "Não foi possível iniciar o Bluetooth LE", exception));
            throw;
        }
    }

    public void Stop()
    {
        if (disposed)
        {
            return;
        }

        cleanupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created)
        {
            watcher.Stop();
        }

        if (publisher.Status is BluetoothLEAdvertisementPublisherStatus.Started or BluetoothLEAdvertisementPublisherStatus.Waiting)
        {
            publisher.Stop();
        }

        StatusChanged?.Invoke(this, new BleDiscoveryStatus(false, "Descoberta Bluetooth pausada"));
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.DataSections)
        {
            if (section.DataType != VeyroBluetoothProtocol.ServiceData128BitUuidType)
            {
                continue;
            }

            var bytes = section.Data.ToArray();
            if (bytes.Length != serviceUuidBytes.Length + BleAdvertisementCodec.EncodedLength ||
                !bytes.AsSpan(0, serviceUuidBytes.Length).SequenceEqual(serviceUuidBytes) ||
                !BleAdvertisementCodec.TryDecode(bytes.AsSpan(serviceUuidBytes.Length), out var advertisement) ||
                advertisement is null ||
                string.Equals(advertisement.EphemeralId, localEphemeralId, StringComparison.Ordinal))
            {
                continue;
            }

            registry.Observe(
                advertisement,
                args.BluetoothAddress,
                args.RawSignalStrengthInDBm,
                DateTimeOffset.UtcNow);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            break;
        }
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (!disposed)
        {
            StatusChanged?.Invoke(
                this,
                new BleDiscoveryStatus(false, $"Varredura Bluetooth encerrada: {args.Error}"));
        }
    }

    private void Publisher_StatusChanged(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
    {
        if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
        {
            StatusChanged?.Invoke(
                this,
                new BleDiscoveryStatus(false, $"Anúncio Bluetooth interrompido: {args.Error}"));
        }
    }

    private void RemoveExpiredDevices(object? state)
    {
        if (registry.RemoveExpired(DateTimeOffset.UtcNow) > 0)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        disposed = true;
        watcher.Received -= Watcher_Received;
        watcher.Stopped -= Watcher_Stopped;
        publisher.StatusChanged -= Publisher_StatusChanged;
        cleanupTimer.Dispose();
    }
}
