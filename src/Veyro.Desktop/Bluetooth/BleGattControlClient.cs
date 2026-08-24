using System.Runtime.InteropServices.WindowsRuntime;
using Veyro.Desktop.Core.Pairing;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Veyro.Desktop.Bluetooth;

public sealed class BleGattControlClient : IDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? controlCharacteristic;
    private bool disposed;
    private bool initialConnection;
    private int restoreQueued;

    public event EventHandler<BleControlPacketEventArgs>? PacketReceived;

    public event EventHandler? ChannelRestored;

    public event EventHandler<Exception>? ChannelRestoreFailed;

    public async Task ConnectAsync(ulong bluetoothAddress)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await connectionGate.WaitAsync();
        try
        {
            initialConnection = true;
            Disconnect();
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                ?? throw new InvalidOperationException("O dispositivo Bluetooth não está mais disponível.");
            device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            device.GattServicesChanged += Device_GattServicesChanged;
            await InitializeGattAsync();
        }
        finally
        {
            initialConnection = false;
            connectionGate.Release();
        }
    }

    private async Task InitializeGattAsync()
    {
        var activeDevice = device ?? throw new InvalidOperationException("O dispositivo BLE não está disponível.");
        ClearGattState();
        var serviceResult = await activeDevice.GetGattServicesForUuidAsync(
            VeyroBluetoothProtocol.ServiceUuid,
            BluetoothCacheMode.Uncached);
        if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
        {
            Disconnect();
            throw new InvalidOperationException($"O serviço Veyro não respondeu: {serviceResult.Status}.");
        }

        service = serviceResult.Services[0];
        var characteristicResult = await service.GetCharacteristicsForUuidAsync(
            VeyroBluetoothProtocol.ControlCharacteristicUuid,
            BluetoothCacheMode.Uncached);
        if (characteristicResult.Status != GattCommunicationStatus.Success || characteristicResult.Characteristics.Count == 0)
        {
            Disconnect();
            throw new InvalidOperationException($"O canal de controle Veyro não respondeu: {characteristicResult.Status}.");
        }

        controlCharacteristic = characteristicResult.Characteristics[0];
        controlCharacteristic.ValueChanged += ControlCharacteristic_ValueChanged;
        var subscriptionStatus = await controlCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (subscriptionStatus != GattCommunicationStatus.Success)
        {
            Disconnect();
            throw new InvalidOperationException($"Não foi possível assinar respostas do canal Veyro: {subscriptionStatus}.");
        }
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!initialConnection && sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            QueueChannelRestore();
        }
    }

    private void Device_GattServicesChanged(BluetoothLEDevice sender, object args)
    {
        if (!initialConnection)
        {
            QueueChannelRestore();
        }
    }

    private async void QueueChannelRestore()
    {
        if (disposed || Interlocked.Exchange(ref restoreQueued, 1) != 0)
        {
            return;
        }

        try
        {
            await Task.Delay(500);
            await connectionGate.WaitAsync();
            try
            {
                if (device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    return;
                }

                await InitializeGattAsync();
                ChannelRestored?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                connectionGate.Release();
            }
        }
        catch (Exception exception)
        {
            ChannelRestoreFailed?.Invoke(this, exception);
        }
        finally
        {
            Interlocked.Exchange(ref restoreQueued, 0);
        }
    }

    public async Task SendAsync(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length is 0 or > PairingMessageCodec.MaximumBleControlPacketSize)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        if (controlCharacteristic is null)
        {
            throw new InvalidOperationException("No Veyro BLE peer is connected.");
        }

        var result = await controlCharacteristic.WriteValueWithResultAsync(
            packet.AsBuffer(),
            GattWriteOption.WriteWithResponse);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"O envio pelo canal BLE falhou: {result.Status}.");
        }
    }

    private void ControlCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var packet = args.CharacteristicValue.ToArray();
        if (packet.Length is > 0 and <= PairingMessageCodec.MaximumBleControlPacketSize)
        {
            PacketReceived?.Invoke(this, new BleControlPacketEventArgs(packet));
        }
    }

    private void Disconnect()
    {
        if (device is not null)
        {
            device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            device.GattServicesChanged -= Device_GattServicesChanged;
        }
        ClearGattState();
        device?.Dispose();
        device = null;
    }

    private void ClearGattState()
    {
        if (controlCharacteristic is not null)
        {
            controlCharacteristic.ValueChanged -= ControlCharacteristic_ValueChanged;
        }

        controlCharacteristic = null;
        service?.Dispose();
        service = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disconnect();
        connectionGate.Dispose();
    }
}
