using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Pairing;

namespace Veyro.Desktop.Core.Tests;

public sealed class PairingSessionTests
{
    [Fact]
    public void Both_peers_derive_the_same_pin_and_require_bilateral_confirmation()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var firstIdentity = new LocalIdentity("1111111111111111", "Notebook", 1);
        var secondIdentity = new LocalIdentity("2222222222222222", "Celular", 2);
        var firstKey = CreateIdentityKey();
        var secondKey = CreateIdentityKey();
        using var first = PairingSession.Create(
            firstIdentity,
            firstKey,
            VeyroCapability.BleControl,
            "pairing-session",
            createdAt);
        using var second = PairingSession.Create(
            secondIdentity,
            secondKey,
            VeyroCapability.BleControl,
            "pairing-session",
            createdAt);

        var firstVerification = first.AcceptRemoteHello(second.LocalHello, createdAt);
        var secondVerification = second.AcceptRemoteHello(first.LocalHello, createdAt);

        Assert.Equal(firstVerification.Pin, secondVerification.Pin);
        Assert.Matches("^[0-9]{6}$", firstVerification.Pin);

        var firstConfirmation = first.CreateConfirmation(true);
        var secondConfirmation = second.CreateConfirmation(true);
        first.AcceptRemoteConfirmation(secondConfirmation);
        second.AcceptRemoteConfirmation(firstConfirmation);

        var trustedByFirst = first.CreateTrustedDevice(createdAt);
        var trustedBySecond = second.CreateTrustedDevice(createdAt);
        Assert.Equal(secondIdentity.DeviceId, trustedByFirst.DeviceId);
        Assert.Equal(firstIdentity.DeviceId, trustedBySecond.DeviceId);
        Assert.True(first.IsMutuallyConfirmed);
        Assert.True(second.IsMutuallyConfirmed);
    }

    [Fact]
    public void Tampered_hello_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var first = PairingSession.Create(
            new LocalIdentity("1111111111111111", "Notebook", 1),
            CreateIdentityKey(),
            VeyroCapability.BleControl,
            "pairing-session",
            now);
        using var second = PairingSession.Create(
            new LocalIdentity("2222222222222222", "Celular", 2),
            CreateIdentityKey(),
            VeyroCapability.BleControl,
            "pairing-session",
            now);

        var tampered = second.LocalHello with { DisplayName = "Atacante" };

        Assert.Throws<PairingProtocolException>(() => first.AcceptRemoteHello(tampered, now));
    }

    [Fact]
    public void Pairing_control_message_round_trips_through_protobuf()
    {
        var now = DateTimeOffset.UtcNow;
        using var session = PairingSession.Create(
            new LocalIdentity("1111111111111111", "Notebook", 1),
            CreateIdentityKey(),
            VeyroCapability.BleControl,
            "pairing-session",
            now);

        var encoded = PairingMessageCodec.EncodeHello(session.LocalHello);
        var packet = PairingMessageCodec.Decode(encoded);
        var decoded = PairingMessageCodec.ToCore(packet.PairingHello);

        Assert.InRange(encoded.Length, 1, PairingMessageCodec.MaximumBleControlPacketSize);
        Assert.Equal(64, session.LocalHello.Signature.Length);
        Assert.Equal(session.LocalHello.PairingId, decoded.PairingId);
        Assert.Equal(session.LocalHello.DeviceId, decoded.DeviceId);
        Assert.Equal(session.LocalHello.Signature, decoded.Signature);
    }

    internal static LocalIdentityKey CreateIdentityKey()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return new LocalIdentityKey(key.ExportPkcs8PrivateKey(), key.ExportSubjectPublicKeyInfo());
    }
}
