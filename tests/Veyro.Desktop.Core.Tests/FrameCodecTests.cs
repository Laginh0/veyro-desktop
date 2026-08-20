using System.Buffers.Binary;
using Veyro.Desktop.Core.Protocol;

namespace Veyro.Desktop.Core.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public async Task Frame_roundTrips_across_fragmented_reads()
    {
        var payload = "veyro-control-message"u8.ToArray();
        await using var encoded = new MemoryStream();
        await FrameCodec.WriteAsync(encoded, payload, flags: 3);

        await using var fragmented = new FragmentedReadStream(encoded.ToArray(), maximumChunkSize: 2);
        var decoded = await FrameCodec.ReadAsync(fragmented);

        Assert.NotNull(decoded);
        Assert.Equal(3, decoded.Flags);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public async Task Empty_stream_returns_no_frame()
    {
        await using var stream = new MemoryStream();
        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Invalid_magic_is_rejected()
    {
        var bytes = CreateHeader(payloadLength: 0);
        bytes[0] = (byte)'X';

        await Assert.ThrowsAsync<FrameProtocolException>(async () =>
            await FrameCodec.ReadAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_before_allocation()
    {
        var bytes = CreateHeader(payloadLength: 4096);

        await Assert.ThrowsAsync<FrameProtocolException>(async () =>
            await FrameCodec.ReadAsync(new MemoryStream(bytes), maximumPayloadLength: 128));
    }

    [Fact]
    public async Task Truncated_payload_is_rejected()
    {
        var bytes = CreateHeader(payloadLength: 4).Concat(new byte[] { 1, 2 }).ToArray();

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FrameCodec.ReadAsync(new MemoryStream(bytes)));
    }

    private static byte[] CreateHeader(uint payloadLength)
    {
        var bytes = new byte[FrameCodec.HeaderLength];
        "VYRO"u8.CopyTo(bytes);
        bytes[4] = FrameCodec.CurrentVersion;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), payloadLength);
        return bytes;
    }

    private sealed class FragmentedReadStream(byte[] buffer, int maximumChunkSize) : MemoryStream(buffer)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(destination[..Math.Min(destination.Length, maximumChunkSize)], cancellationToken);
    }
}
