using System.Runtime.InteropServices.WindowsRuntime;
using Veyro.Desktop.Core.Pairing;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Veyro.Desktop.Bluetooth;

public sealed class BleGattControlServer : IDisposable
{
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? controlCharacteristic;
    private bool disposed;

    public event EventHandler<BleControlPacketEventArgs>? PacketReceived;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (provider is not null)
        {
            return;
        }

        var providerResult = await GattServiceProvider.CreateAsync(VeyroBluetoothProtocol.ServiceUuid);
        if (providerResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"Não foi possível criar o serviço GATT: {providerResult.Error}.");
        }

        provider = providerResult.ServiceProvider;
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties =
                GattCharacteristicProperties.Write |
                GattCharacteristicProperties.WriteWithoutResponse |
                GattCharacteristicProperties.Notify,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "Veyro BLE control"
        };
        var characteristicResult = await provider.Service.CreateCharacteristicAsync(
            VeyroBluetoothProtocol.ControlCharacteristicUuid,
            parameters);
        if (characteristicResult.Error != BluetoothError.Success)
        {
            provider = null;
            throw new InvalidOperationException($"Não foi possível criar a característica GATT: {characteristicResult.Error}.");
        }

        controlCharacteristic = characteristicResult.Characteristic;
        controlCharacteristic.WriteRequested += ControlCharacteristic_WriteRequested;
        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        });
    }

    public async Task NotifyAsync(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length is 0 or > PairingMessageCodec.MaximumBleControlPacketSize)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        if (controlCharacteristic is null)
        {
            throw new InvalidOperationException("The GATT control service is not running.");
        }

        _ = await controlCharacteristic.NotifyValueAsync(packet.AsBuffer());
    }

    private async void ControlCharacteristic_WriteRequested(
        GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null)
            {
                return;
            }

            var packet = request.Value.ToArray();
            if (packet.Length is > 0 and <= PairingMessageCodec.MaximumBleControlPacketSize)
            {
                PacketReceived?.Invoke(this, new BleControlPacketEventArgs(packet));
                request.Respond();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (controlCharacteristic is not null)
        {
            controlCharacteristic.WriteRequested -= ControlCharacteristic_WriteRequested;
        }

        provider?.StopAdvertising();
        controlCharacteristic = null;
        provider = null;
    }
}
