using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Pairing;
using Veyro.Desktop.Core.Transport;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Tests;

public sealed class SecureFastChannelTests
{
    [Fact]
    public async Task Trusted_peers_exchange_protocol_packets_over_mutual_tls()
    {
        var firstIdentity = new LocalIdentity("1111111111111111", "Notebook", 1);
        var secondIdentity = new LocalIdentity("2222222222222222", "Celular", 2);
        var firstKey = PairingSessionTests.CreateIdentityKey();
        var secondKey = PairingSessionTests.CreateIdentityKey();
        using var firstCertificate = VeyroTlsIdentity.CreateCertificate(firstIdentity, firstKey);
        using var secondCertificate = VeyroTlsIdentity.CreateCertificate(secondIdentity, secondKey);
        var firstTrustsSecond = CreateTrustedDevice(secondIdentity, secondKey);
        var secondTrustsFirst = CreateTrustedDevice(firstIdentity, firstKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (first, second) = await ConnectPairAsync(
            firstIdentity,
            firstCertificate,
            firstTrustsSecond,
            secondIdentity,
            secondCertificate,
            secondTrustsFirst,
            timeout.Token);
        await using (first)
        await using (second)
        {
            await Task.WhenAll(
                first.PerformProtocolHandshakeAsync("session-1", timeout.Token),
                second.PerformProtocolHandshakeAsync("session-1", timeout.Token));
            var resumeRegistry = new FastChannelResumeRegistry();
            var resumeState = resumeRegistry.Create(firstIdentity.DeviceId);
            await Task.WhenAll(
                first.RequestResumeAsync(
                    resumeState.SessionId,
                    resumeState.ResumeToken,
                    cancellationToken: timeout.Token),
                second.AcceptResumeAsync(resumeRegistry, timeout.Token));

            await first.SendAsync(
                new Veyro.Protocol.FastChannelPacket
                {
                    KeepAlive = new Veyro.Protocol.KeepAlive { Sequence = 42, SentAtUnixMs = 10 }
                },
                timeout.Token);
            var received = await second.ReceiveAsync(timeout.Token);

            Assert.NotNull(received);
            Assert.Equal((ulong)42, received.KeepAlive.Sequence);
            Assert.Equal(SslProtocols.Tls13, first.NegotiatedProtocol);
            Assert.Equal(firstIdentity.DeviceId, second.RemoteDeviceId);
            Assert.Equal(secondIdentity.DeviceId, first.RemoteDeviceId);

            using var keepAliveLifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
            var firstKeepAlive = first.RunAsync(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                keepAliveLifetime.Token);
            var secondKeepAlive = second.RunAsync(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                keepAliveLifetime.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await Task.WhenAll(firstKeepAlive, secondKeepAlive));
        }
    }

    [Fact]
    public void Certificate_is_rejected_when_public_key_does_not_match_trust_hub()
    {
        var identity = new LocalIdentity("1111111111111111", "Notebook", 1);
        var actualKey = PairingSessionTests.CreateIdentityKey();
        var differentKey = PairingSessionTests.CreateIdentityKey();
        using var certificate = VeyroTlsIdentity.CreateCertificate(identity, actualKey);
        var incorrectTrust = CreateTrustedDevice(identity, differentKey);

        Assert.False(VeyroTlsIdentity.ValidatePeerCertificate(certificate, incorrectTrust));
    }

    [Fact]
    public void Resume_token_is_scoped_expires_and_never_moves_sequence_backwards()
    {
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var registry = new FastChannelResumeRegistry(TimeSpan.FromMinutes(5));
        var state = registry.Create("2222222222222222", now);

        Assert.True(registry.TryResume(
            state.SessionId,
            state.RemoteDeviceId,
            state.ResumeToken,
            now.AddMinutes(4),
            out var resumed));
        var renewed = Assert.IsType<FastChannelResumeState>(resumed);
        Assert.Equal(state.SessionId, renewed.SessionId);
        Assert.Equal(state.RemoteDeviceId, renewed.RemoteDeviceId);
        Assert.Equal(state.ResumeToken, renewed.ResumeToken);
        Assert.Equal(now.AddMinutes(9), renewed.ExpiresAt);
        Assert.True(registry.UpdateSequence(state.SessionId, 10));
        Assert.False(registry.UpdateSequence(state.SessionId, 9));
        Assert.False(registry.TryResume(
            state.SessionId,
            "3333333333333333",
            state.ResumeToken,
            now,
            out _));
        Assert.Equal(1, registry.RemoveExpired(now.AddMinutes(10)));
    }

    [Fact]
    public void Fast_channel_offer_is_signed_scoped_and_fits_ble_control_packet()
    {
        var identity = new LocalIdentity("1111111111111111", "Notebook", 1);
        var key = PairingSessionTests.CreateIdentityKey();
        var trusted = CreateTrustedDevice(identity, key);
        var resumeState = new FastChannelResumeRegistry().Create("2222222222222222");
        var offer = FastChannelOfferSigner.Create(
            identity,
            key,
            Veyro.Protocol.FastChannelRole.GroupOwner,
            45678,
            resumeState,
            "2222222222222222");

        var encoded = PairingMessageCodec.EncodeFastChannelOffer(offer);
        var tampered = offer.Clone();
        tampered.TcpPort = 45679;
        var retargeted = offer.Clone();
        retargeted.TargetDeviceId = "3333333333333333";

        Assert.True(FastChannelOfferSigner.Validate(offer, trusted));
        Assert.True(FastChannelOfferSigner.Validate(offer, trusted, "2222222222222222"));
        Assert.False(FastChannelOfferSigner.Validate(offer, trusted, "3333333333333333"));
        Assert.InRange(encoded.Length, 1, PairingMessageCodec.MaximumBleControlPacketSize);
        Assert.False(FastChannelOfferSigner.Validate(tampered, trusted));
        Assert.False(FastChannelOfferSigner.Validate(retargeted, trusted));
    }

    private static async Task<(SecureFastChannel First, SecureFastChannel Second)> ConnectPairAsync(
        LocalIdentity firstIdentity,
        System.Security.Cryptography.X509Certificates.X509Certificate2 firstCertificate,
        TrustedDevice firstTrustsSecond,
        LocalIdentity secondIdentity,
        System.Security.Cryptography.X509Certificates.X509Certificate2 secondCertificate,
        TrustedDevice secondTrustsFirst,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var acceptTcpTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var firstTcp = new TcpClient(AddressFamily.InterNetwork);
            await firstTcp.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken);
            var secondTcp = await acceptTcpTask;

            var acceptTask = SecureFastChannel.AcceptAsync(
                secondTcp,
                secondIdentity.DeviceId,
                secondCertificate,
                deviceId => string.Equals(deviceId, secondTrustsFirst.DeviceId, StringComparison.Ordinal)
                    ? secondTrustsFirst
                    : null,
                cancellationToken);
            var connectTask = SecureFastChannel.ConnectAsync(
                firstTcp,
                firstIdentity.DeviceId,
                firstCertificate,
                firstTrustsSecond,
                cancellationToken);
            await Task.WhenAll(acceptTask, connectTask);
            return (await connectTask, await acceptTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static TrustedDevice CreateTrustedDevice(LocalIdentity identity, LocalIdentityKey identityKey) =>
        new(
            identity.DeviceId,
            identity.DisplayName,
            Convert.ToBase64String(identityKey.PublicKeySpki),
            VeyroCapability.BleControl | VeyroCapability.WifiDirectData,
            1,
            1,
            null);
}
