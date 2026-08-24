using System.Security.Cryptography;
using Google.Protobuf;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Protocol;
using Veyro.Desktop.Core.Routing;
using Veyro.Desktop.Core.Trust;
using Veyro.Protocol;

namespace Veyro.Desktop.Core.Tests;

public sealed class LogicalRouterTests
{
    [Fact]
    public void Coordinator_forwards_opaque_android_payload_only_to_the_target_android()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var localKey = new LocalIdentityKey(
            key.ExportPkcs8PrivateKey(),
            key.ExportSubjectPublicKeyInfo());
        var origin = new TrustedDevice(
            "android-a",
            "Android A",
            Convert.ToBase64String(localKey.PublicKeySpki),
            VeyroCapability.MultiDeviceRouting,
            1,
            1,
            null);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var envelope = new TransportEnvelope
        {
            ProtocolMajor = ProtocolContract.TransportMajor,
            ProtocolMinor = ProtocolContract.TransportMinor,
            MessageId = Guid.NewGuid().ToString("D"),
            OriginDeviceId = origin.DeviceId,
            PayloadType = TransportPayloadType.ApplicationMessage,
            CreatedAtUnixMs = now,
            ExpiresAtUnixMs = now + 30_000,
            RemainingHops = ProtocolContract.DefaultHopLimit,
            SequenceNumber = 1,
            EncryptedPayload = ByteString.CopyFromUtf8("opaque-ciphertext")
        };
        envelope.DestinationDeviceIds.Add("android-b");
        TransportEnvelopeSigner.Sign(envelope, localKey);
        var router = new LogicalRouter(
            "desktop",
            new EnvelopeDeduplicator(),
            deviceId => deviceId == origin.DeviceId ? origin : null);

        var decision = router.RouteIncoming(
            envelope,
            ingressDeviceId: origin.DeviceId,
            connectedDeviceIds: ["android-a", "android-b", "android-c"],
            localIsCoordinator: true,
            nowUnixMilliseconds: now);

        Assert.False(decision.IsRejected);
        Assert.False(decision.DeliverLocally);
        Assert.Equal(["android-b"], decision.ForwardTargets);
        Assert.Equal(envelope.EncryptedPayload, decision.ForwardEnvelope!.EncryptedPayload);
        Assert.Equal(envelope.RemainingHops - 1, decision.ForwardEnvelope.RemainingHops);
    }
}
