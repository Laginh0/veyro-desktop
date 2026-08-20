using Google.Protobuf;
using Veyro.Desktop.Core.Protocol;
using Veyro.Protocol;

namespace Veyro.Desktop.Core.Tests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void Existing_android_message_roundTrips_inside_transport_payload()
    {
        var applicationMessage = new VeyroMessage
        {
            ProtocolVersion = ProtocolContract.AndroidFeatureProtocolVersion,
            PingEvent = new PingEvent { RequestId = "ping-1", Action = PingAction.PingRequest }
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var envelope = CreateEnvelope(now, ByteString.CopyFrom(applicationMessage.ToByteArray()));

        var decodedEnvelope = TransportEnvelope.Parser.ParseFrom(envelope.ToByteArray());
        var decodedMessage = VeyroMessage.Parser.ParseFrom(decodedEnvelope.EncryptedPayload);

        Assert.Equal(PingAction.PingRequest, decodedMessage.PingEvent.Action);
        Assert.True(TransportEnvelopeValidator.Validate(decodedEnvelope, now).IsValid);
    }

    [Fact]
    public void Unknown_major_version_is_rejected_safely()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var envelope = CreateEnvelope(now, ByteString.CopyFromUtf8("opaque"));
        envelope.ProtocolMajor = 99;

        var result = TransportEnvelopeValidator.Validate(envelope, now);

        Assert.False(result.IsValid);
        Assert.Equal("unsupported_version", result.ErrorCode);
    }

    [Fact]
    public void Expired_envelope_is_rejected()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var envelope = CreateEnvelope(now - 10_000, ByteString.CopyFromUtf8("opaque"));
        envelope.ExpiresAtUnixMs = now - 1;

        var result = TransportEnvelopeValidator.Validate(envelope, now);

        Assert.False(result.IsValid);
        Assert.Equal("expired", result.ErrorCode);
    }

    private static TransportEnvelope CreateEnvelope(long createdAt, ByteString payload)
    {
        var envelope = new TransportEnvelope
        {
            ProtocolMajor = ProtocolContract.TransportMajor,
            ProtocolMinor = ProtocolContract.TransportMinor,
            MessageId = Guid.NewGuid().ToString("D"),
            OriginDeviceId = "abcdef1234567890",
            PayloadType = TransportPayloadType.ApplicationMessage,
            CreatedAtUnixMs = createdAt,
            ExpiresAtUnixMs = createdAt + 30_000,
            RemainingHops = ProtocolContract.DefaultHopLimit,
            EncryptedPayload = payload
        };
        envelope.DestinationDeviceIds.Add("0123456789abcdef");
        return envelope;
    }
}
