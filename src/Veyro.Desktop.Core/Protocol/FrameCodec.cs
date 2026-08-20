using System.Buffers.Binary;

namespace Veyro.Desktop.Core.Protocol;

public static class FrameCodec
{
    public const byte CurrentVersion = 1;
    public const int HeaderLength = 12;
    public const int DefaultMaximumPayloadLength = 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "VYRO"u8;

    public static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        byte flags = 0,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > DefaultMaximumPayloadLength)
        {
            throw new FrameProtocolException("The frame payload exceeds the control-channel limit.");
        }

        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = CurrentVersion;
        header[5] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), checked((uint)payload.Length));

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Frame?> ReadAsync(
        Stream stream,
        int maximumPayloadLength = DefaultMaximumPayloadLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadLength);

        var header = new byte[HeaderLength];
        var headerBytes = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != HeaderLength)
        {
            throw new EndOfStreamException("The Veyro frame header was truncated.");
        }

        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new FrameProtocolException("The frame magic is invalid.");
        }

        if (header[4] != CurrentVersion)
        {
            throw new FrameProtocolException("The frame version is unsupported.");
        }

        if (header[6] != 0 || header[7] != 0)
        {
            throw new FrameProtocolException("Reserved frame header bytes must be zero.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > maximumPayloadLength)
        {
            throw new FrameProtocolException("The frame payload exceeds the configured limit.");
        }

        var payload = new byte[checked((int)payloadLength)];
        var payloadBytes = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes != payload.Length)
        {
            throw new EndOfStreamException("The Veyro frame payload was truncated.");
        }

        return new Frame(header[5], payload);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        return total;
    }
}
