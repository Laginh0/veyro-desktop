using System.Runtime.InteropServices.WindowsRuntime;
using Veyro.Desktop.Core.Pairing;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Veyro.Desktop.Bluetooth;

public sealed class BleGattControlClient : IDisposable
{
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? controlCharacteristic;
    private bool disposed;

    public event EventHandler<BleControlPacketEventArgs>? PacketReceived;

    public async Task ConnectAsync(ulong bluetoothAddress)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Disconnect();

        device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
            ?? throw new InvalidOperationException("O dispositivo Bluetooth não está mais disponível.");
        var serviceResult = await device.GetGattServicesForUuidAsync(
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
        if (controlCharacteristic is not null)
        {
            controlCharacteristic.ValueChanged -= ControlCharacteristic_ValueChanged;
        }

        controlCharacteristic = null;
        service?.Dispose();
        service = null;
        device?.Dispose();
        device = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disconnect();
    }
}
