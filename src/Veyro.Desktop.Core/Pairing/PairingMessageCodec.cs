using Google.Protobuf;

namespace Veyro.Desktop.Core.Pairing;

public static class PairingMessageCodec
{
    public const int MaximumBleControlPacketSize = 512;

    public static byte[] EncodeHello(PairingHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var packet = new Veyro.Protocol.BleControlPacket
        {
            PairingHello = new Veyro.Protocol.PairingHello
            {
                PairingId = hello.PairingId,
                DeviceId = hello.DeviceId,
                DisplayName = hello.DisplayName,
                Capabilities = (uint)hello.Capabilities,
                CreatedAtUnixMs = hello.CreatedAtUnixMilliseconds,
                Nonce = ByteString.CopyFrom(hello.Nonce),
                IdentityPublicKeySpki = ByteString.CopyFrom(hello.IdentityPublicKeySpki),
                EphemeralPublicKeySpki = ByteString.CopyFrom(hello.EphemeralPublicKeySpki),
                Signature = ByteString.CopyFrom(hello.Signature)
            }
        };
        return Encode(packet);
    }

    public static byte[] EncodeConfirmation(PairingConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var packet = new Veyro.Protocol.BleControlPacket
        {
            PairingConfirmation = new Veyro.Protocol.PairingConfirmation
            {
                PairingId = confirmation.PairingId,
                Accepted = confirmation.Accepted,
                VerificationDigest = ByteString.CopyFrom(confirmation.VerificationDigest),
                Signature = ByteString.CopyFrom(confirmation.Signature)
            }
        };
        return Encode(packet);
    }

    public static byte[] EncodeReconnectChallenge(string requestingDeviceId, byte[] challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingDeviceId);
        ArgumentNullException.ThrowIfNull(challenge);
        var packet = new Veyro.Protocol.BleControlPacket
        {
            ReconnectChallenge = new Veyro.Protocol.ReconnectChallenge
            {
                RequestingDeviceId = requestingDeviceId,
                Challenge = ByteString.CopyFrom(challenge)
            }
        };
        return Encode(packet);
    }

    public static byte[] EncodeReconnectProof(string deviceId, byte[] challenge, byte[] signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(signature);
        var packet = new Veyro.Protocol.BleControlPacket
        {
            ReconnectProof = new Veyro.Protocol.ReconnectProof
            {
                DeviceId = deviceId,
                Challenge = ByteString.CopyFrom(challenge),
                Signature = ByteString.CopyFrom(signature)
            }
        };
        return Encode(packet);
    }

    public static Veyro.Protocol.BleControlPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBleControlPacketSize)
        {
            throw new PairingProtocolException("The BLE control packet size is invalid.");
        }

        try
        {
            var packet = Veyro.Protocol.BleControlPacket.Parser.ParseFrom(bytes);
            if (packet.BodyCase == Veyro.Protocol.BleControlPacket.BodyOneofCase.None)
            {
                throw new PairingProtocolException("The BLE control packet has no body.");
            }

            return packet;
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new PairingProtocolException($"The BLE control packet is malformed: {exception.Message}");
        }
    }

    public static PairingHello ToCore(Veyro.Protocol.PairingHello hello) =>
        new(
            hello.PairingId,
            hello.DeviceId,
            hello.DisplayName,
            (Discovery.VeyroCapability)hello.Capabilities,
            hello.CreatedAtUnixMs,
            hello.Nonce.ToByteArray(),
            hello.IdentityPublicKeySpki.ToByteArray(),
            hello.EphemeralPublicKeySpki.ToByteArray(),
            hello.Signature.ToByteArray());

    public static PairingConfirmation ToCore(Veyro.Protocol.PairingConfirmation confirmation) =>
        new(
            confirmation.PairingId,
            confirmation.Accepted,
            confirmation.VerificationDigest.ToByteArray(),
            confirmation.Signature.ToByteArray());

    private static byte[] Encode(Veyro.Protocol.BleControlPacket packet)
    {
        var bytes = packet.ToByteArray();
        if (bytes.Length > MaximumBleControlPacketSize)
        {
            throw new PairingProtocolException("The BLE control packet exceeds the safe limit.");
        }

        return bytes;
    }
}
