using Veyro.Protocol;

namespace Veyro.Desktop.Core.Protocol;

public static class TransportEnvelopeValidator
{
    public static EnvelopeValidationResult Validate(TransportEnvelope envelope, long nowUnixMilliseconds)
    {
        if (envelope.ProtocolMajor != ProtocolContract.TransportMajor)
        {
            return EnvelopeValidationResult.Invalid("unsupported_version");
        }

        if (!Guid.TryParseExact(envelope.MessageId, "D", out _))
        {
            return EnvelopeValidationResult.Invalid("invalid_message_id");
        }

        if (string.IsNullOrWhiteSpace(envelope.OriginDeviceId))
        {
            return EnvelopeValidationResult.Invalid("missing_origin");
        }

        if (envelope.AuthorizedBroadcast == (envelope.DestinationDeviceIds.Count > 0))
        {
            return EnvelopeValidationResult.Invalid("invalid_destination");
        }

        if (envelope.CreatedAtUnixMs <= 0 || envelope.ExpiresAtUnixMs < envelope.CreatedAtUnixMs)
        {
            return EnvelopeValidationResult.Invalid("invalid_validity_window");
        }

        if (envelope.ExpiresAtUnixMs < nowUnixMilliseconds)
        {
            return EnvelopeValidationResult.Invalid("expired");
        }

        if (envelope.RemainingHops is 0 or > ProtocolContract.DefaultHopLimit)
        {
            return EnvelopeValidationResult.Invalid("invalid_hop_limit");
        }

        if (envelope.PayloadType == TransportPayloadType.PayloadUnspecified || envelope.EncryptedPayload.IsEmpty)
        {
            return EnvelopeValidationResult.Invalid("missing_payload");
        }

        return EnvelopeValidationResult.Valid;
    }
}
