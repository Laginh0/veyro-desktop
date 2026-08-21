namespace Veyro.Desktop.Bluetooth;

public static class VeyroBluetoothProtocol
{
    private static readonly byte[] serviceDataUuidBytes =
        Convert.FromHexString("FFD86C4C809C8895A54C6D265E92D068");

    // UUIDs are Veyro-owned 128-bit identifiers and are not Bluetooth SIG short UUIDs.
    public static readonly Guid ServiceUuid = new("68d0925e-266d-4ca5-9588-9c804c6cd8ff");
    public static readonly Guid ControlCharacteristicUuid = new("886c164a-9f9f-465f-9428-8fb7ee8cd15a");

    public const byte ServiceData128BitUuidType = 0x21;

    // Bluetooth advertising serializes a 128-bit UUID least-significant byte first.
    public static ReadOnlySpan<byte> ServiceDataUuidBytes => serviceDataUuidBytes;
}
