using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.ExceptionServices;
using Google.Protobuf;
using Veyro.Desktop.Core.Protocol;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Transport;

public sealed class SecureFastChannel : IAsyncDisposable
{
    private readonly TcpClient tcpClient;
    private readonly SslStream stream;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly TimeProvider timeProvider;
    private long lastReceivedTimestamp;
    private ulong keepAliveSequence;
    private bool disposed;

    private SecureFastChannel(
        TcpClient tcpClient,
        SslStream stream,
        string localDeviceId,
        string remoteDeviceId,
        TimeProvider? timeProvider = null)
    {
        this.tcpClient = tcpClient;
        this.stream = stream;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        LocalDeviceId = localDeviceId;
        RemoteDeviceId = remoteDeviceId;
        lastReceivedTimestamp = this.timeProvider.GetTimestamp();
    }

    public event EventHandler<FastChannelPacketEventArgs>? PacketReceived;

    public string LocalDeviceId { get; }

    public string RemoteDeviceId { get; }

    public SslProtocols NegotiatedProtocol => stream.SslProtocol;

    public static async Task<SecureFastChannel> ConnectAsync(
        TcpClient tcpClient,
        string localDeviceId,
        X509Certificate2 localCertificate,
        TrustedDevice expectedPeer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tcpClient);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedPeer);

        var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) => VeyroTlsIdentity.ValidatePeerCertificate(certificate, expectedPeer));
        try
        {
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = expectedPeer.DeviceId,
                    ClientCertificates = new X509CertificateCollection { localCertificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [VeyroTlsIdentity.ApplicationProtocol],
                    AllowRenegotiation = false
                },
                cancellationToken).ConfigureAwait(false);
            if (sslStream.NegotiatedApplicationProtocol != VeyroTlsIdentity.ApplicationProtocol)
            {
                throw new AuthenticationException("The peer did not negotiate the Veyro ALPN.");
            }

            return new SecureFastChannel(tcpClient, sslStream, localDeviceId, expectedPeer.DeviceId);
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            tcpClient.Dispose();
            throw;
        }
    }

    public static async Task<SecureFastChannel> AcceptAsync(
        TcpClient tcpClient,
        string localDeviceId,
        X509Certificate2 localCertificate,
        Func<string, TrustedDevice?> trustedDeviceResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tcpClient);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedDeviceResolver);

        TrustedDevice? authenticatedPeer = null;
        var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                using var peerCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                var deviceId = peerCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                authenticatedPeer = trustedDeviceResolver(deviceId);
                return authenticatedPeer is not null &&
                    VeyroTlsIdentity.ValidatePeerCertificate(peerCertificate, authenticatedPeer);
            });
        try
        {
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = localCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [VeyroTlsIdentity.ApplicationProtocol],
                    AllowRenegotiation = false
                },
                cancellationToken).ConfigureAwait(false);
            if (authenticatedPeer is null ||
                sslStream.NegotiatedApplicationProtocol != VeyroTlsIdentity.ApplicationProtocol)
            {
                throw new AuthenticationException("The TLS peer is not present in the Trust Hub.");
            }

            return new SecureFastChannel(tcpClient, sslStream, localDeviceId, authenticatedPeer.DeviceId);
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            tcpClient.Dispose();
            throw;
        }
    }

    public async Task PerformProtocolHandshakeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var hello = new Veyro.Protocol.FastChannelPacket
        {
            Hello = new Veyro.Protocol.FastChannelHello
            {
                SessionId = sessionId,
                DeviceId = LocalDeviceId,
                ProtocolMajor = ProtocolContract.TransportMajor,
                ProtocolMinor = ProtocolContract.TransportMinor
            }
        };

        await SendAsync(hello, cancellationToken).ConfigureAwait(false);
        var remotePacket = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (remotePacket?.BodyCase != Veyro.Protocol.FastChannelPacket.BodyOneofCase.Hello ||
            !string.Equals(remotePacket.Hello.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(remotePacket.Hello.DeviceId, RemoteDeviceId, StringComparison.Ordinal) ||
            remotePacket.Hello.ProtocolMajor != ProtocolContract.TransportMajor)
        {
            throw new AuthenticationException("The fast-channel protocol hello is invalid.");
        }
    }

    public async Task SendAsync(
        Veyro.Protocol.FastChannelPacket packet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(disposed, this);
        var payload = packet.ToByteArray();
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(stream, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task RequestResumeAsync(
        string sessionId,
        byte[] resumeToken,
        ulong lastReceivedSequence = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(resumeToken);
        if (resumeToken.Length != 32)
        {
            throw new ArgumentException("A resume token must contain exactly 32 bytes.", nameof(resumeToken));
        }

        await SendAsync(
            new Veyro.Protocol.FastChannelPacket
            {
                ResumeRequest = new Veyro.Protocol.ResumeRequest
                {
                    PreviousSessionId = sessionId,
                    LastReceivedSequence = lastReceivedSequence,
                    ResumeToken = ByteString.CopyFrom(resumeToken)
                }
            },
            cancellationToken).ConfigureAwait(false);
        var response = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (response?.BodyCase != Veyro.Protocol.FastChannelPacket.BodyOneofCase.ResumeResponse ||
            !response.ResumeResponse.Accepted ||
            !string.Equals(response.ResumeResponse.PreviousSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new AuthenticationException("The fast-channel resume request was rejected.");
        }
    }

    public async Task AcceptResumeAsync(
        FastChannelResumeRegistry resumeRegistry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resumeRegistry);
        var requestPacket = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (requestPacket?.BodyCase != Veyro.Protocol.FastChannelPacket.BodyOneofCase.ResumeRequest)
        {
            throw new AuthenticationException("The fast-channel resume request is missing.");
        }

        var request = requestPacket.ResumeRequest;
        var accepted = resumeRegistry.TryResume(
            request.PreviousSessionId,
            RemoteDeviceId,
            request.ResumeToken.Span,
            timeProvider.GetUtcNow(),
            out var state);
        if (accepted)
        {
            resumeRegistry.UpdateSequence(request.PreviousSessionId, request.LastReceivedSequence);
        }

        await SendAsync(
            new Veyro.Protocol.FastChannelPacket
            {
                ResumeResponse = new Veyro.Protocol.ResumeResponse
                {
                    PreviousSessionId = request.PreviousSessionId,
                    Accepted = accepted,
                    ResumeFromSequence = accepted
                        ? Math.Max(state!.LastReceivedSequence, request.LastReceivedSequence) + 1
                        : 0
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new AuthenticationException("The fast-channel resume token is invalid or expired.");
        }
    }

    public async Task<Veyro.Protocol.FastChannelPacket?> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = await FrameCodec.ReadAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return null;
            }

            lastReceivedTimestamp = timeProvider.GetTimestamp();
            try
            {
                return Veyro.Protocol.FastChannelPacket.Parser.ParseFrom(frame.Payload.ToArray());
            }
            catch (InvalidProtocolBufferException exception)
            {
                throw new FrameProtocolException($"The fast-channel packet is malformed: {exception.Message}");
            }
        }
        finally
        {
            readGate.Release();
        }
    }

    public async Task RunAsync(
        TimeSpan keepAliveInterval,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken = default)
    {
        if (keepAliveInterval <= TimeSpan.Zero || connectionTimeout <= keepAliveInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval));
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveLoopAsync(lifetime.Token);
        var keepAliveTask = KeepAliveLoopAsync(keepAliveInterval, connectionTimeout, lifetime.Token);
        var completed = await Task.WhenAny(receiveTask, keepAliveTask).ConfigureAwait(false);
        lifetime.Cancel();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await completed.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await Task.WhenAll(receiveTask, keepAliveTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        failure?.Throw();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await ReceiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The fast channel was closed by the peer.");
            switch (packet.BodyCase)
            {
                case Veyro.Protocol.FastChannelPacket.BodyOneofCase.KeepAlive:
                    await SendAsync(
                        new Veyro.Protocol.FastChannelPacket
                        {
                            KeepAliveAcknowledgement = new Veyro.Protocol.KeepAliveAcknowledgement
                            {
                                Sequence = packet.KeepAlive.Sequence
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                case Veyro.Protocol.FastChannelPacket.BodyOneofCase.KeepAliveAcknowledgement:
                    break;
                case Veyro.Protocol.FastChannelPacket.BodyOneofCase.None:
                    throw new FrameProtocolException("The fast-channel packet has no body.");
                default:
                    PacketReceived?.Invoke(this, new FastChannelPacketEventArgs(packet));
                    break;
            }
        }
    }

    private async Task KeepAliveLoopAsync(
        TimeSpan interval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (timeProvider.GetElapsedTime(lastReceivedTimestamp) > timeout)
            {
                throw new TimeoutException("The fast channel did not receive a keepalive in time.");
            }

            await SendAsync(
                new Veyro.Protocol.FastChannelPacket
                {
                    KeepAlive = new Veyro.Protocol.KeepAlive
                    {
                        Sequence = ++keepAliveSequence,
                        SentAtUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await stream.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        tcpClient.Dispose();
        writeGate.Dispose();
        readGate.Dispose();
    }
}
