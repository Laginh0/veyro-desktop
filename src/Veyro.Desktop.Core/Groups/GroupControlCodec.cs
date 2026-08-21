using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veyro.Desktop.Core.Groups;

public static class GroupControlCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadLength = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<GroupControlKind>() }
    };

    public static byte[] Encode(GroupControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != CurrentVersion || message.Members.Count > 32)
        {
            throw new InvalidDataException("The group control message exceeds the supported contract.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("The group control payload is too large.");
        }

        return payload;
    }

    public static GroupControlMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("The group control payload size is invalid.");
        }

        try
        {
            var message = JsonSerializer.Deserialize<GroupControlMessage>(payload, JsonOptions)
                ?? throw new InvalidDataException("The group control payload is empty.");
            if (message.Version != CurrentVersion ||
                !Enum.IsDefined(message.Kind) ||
                message.Epoch == 0 ||
                string.IsNullOrWhiteSpace(message.CoordinatorDeviceId) ||
                string.IsNullOrWhiteSpace(message.InitiatorDeviceId) ||
                message.Members.Count is 0 or > 32 ||
                message.Members.Any(member =>
                    string.IsNullOrWhiteSpace(member.DeviceId) ||
                    string.IsNullOrWhiteSpace(member.DisplayName)) ||
                message.Members.Select(member => member.DeviceId).Distinct(StringComparer.Ordinal).Count() !=
                message.Members.Count)
            {
                throw new InvalidDataException("The group control payload is invalid.");
            }

            return message;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The group control payload is malformed.", exception);
        }
    }
}
