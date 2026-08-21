using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Transport;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.Pairing;
using Veyro.Desktop.WifiDirect;

namespace Veyro.Desktop.FastChannel;

public sealed class FastChannelCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private readonly LocalIdentity localIdentity;
    private readonly LocalIdentityKey localIdentityKey;
    private readonly TrustStore trustStore;
    private readonly BlePairingCoordinator bleCoordinator;
    private readonly WifiDirectManager wifiDirectManager;
    private readonly FastChannelResumeRegistry resumeRegistry = new();
    private readonly ConcurrentDictionary<string, SecureFastChannel> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly X509Certificate2 localCertificate;
    private WifiDirectPeerConnection? wifiDirectLink;
    private TcpListener? listener;
    private bool disposed;

    public FastChannelCoordinator(
        LocalIdentity localIdentity,
        LocalIdentityKey localIdentityKey,
        TrustStore trustStore,
        BlePairingCoordinator bleCoordinator,
        WifiDirectManager wifiDirectManager)
    {
        this.localIdentity = localIdentity;
        this.localIdentityKey = localIdentityKey;
        this.trustStore = trustStore;
        this.bleCoordinator = bleCoordinator;
        this.wifiDirectManager = wifiDirectManager;
        localCertificate = VeyroTlsIdentity.CreateCertificate(localIdentity, localIdentityKey);
        wifiDirectManager.PeerConnected += WifiDirectManager_PeerConnected;
        wifiDirectManager.StatusChanged += WifiDirectManager_StatusChanged;
        bleCoordinator.FastChannelOfferReceived += BleCoordinator_FastChannelOfferReceived;
        bleCoordinator.FastChannelAnswerReceived += BleCoordinator_FastChannelAnswerReceived;
    }

    public event EventHandler<FastChannelStatusEventArgs>? StatusChanged;

    public int ActiveSessionCount => sessions.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        wifiDirectManager.Start();
    }

    private async void WifiDirectManager_PeerConnected(object? sender, WifiDirectPeerConnection connection)
    {
        try
        {
            wifiDirectLink = connection;
            var remoteDeviceId = bleCoordinator.ActiveTrustedDeviceId
                ?? throw new InvalidOperationException("O enlace Wi-Fi Direct não possui um par autenticado no BLE.");
            var trustedDevice = trustStore.FindActive(remoteDeviceId)
                ?? throw new InvalidOperationException("O par Wi-Fi Direct não está ativo no Trust Hub.");
            var localAddress = ParseDirectAddress(connection.LocalAddress);

            listener?.Stop();
            listener = new TcpListener(localAddress, 0);
            listener.Start(backlog: 1);
            var port = checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            var resumeState = resumeRegistry.FindActiveForDevice(trustedDevice.DeviceId, DateTimeOffset.UtcNow)
                ?? resumeRegistry.Create(trustedDevice.DeviceId);
            var offer = FastChannelOfferSigner.Create(
                localIdentity,
                localIdentityKey,
                Veyro.Protocol.FastChannelRole.GroupOwner,
                port,
                resumeState);
            await bleCoordinator.SendFastChannelOfferAsync(offer);
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Oferta do canal rápido enviada pelo BLE"));
            _ = AcceptFastChannelAsync(listener, offer.SessionId, lifetime.Token);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Falha ao preparar o canal rápido", exception));
        }
    }

    private async void BleCoordinator_FastChannelOfferReceived(object? sender, FastChannelOfferEventArgs args)
    {
        try
        {
            var offer = args.Offer;
            var trustedDevice = trustStore.FindActive(offer.DeviceId);
            if (trustedDevice is null || !FastChannelOfferSigner.Validate(offer, trustedDevice))
            {
                await bleCoordinator.SendFastChannelAnswerAsync(offer.SessionId, false, "unauthorized");
                throw new InvalidOperationException("A oferta do canal rápido não pertence a um dispositivo confiável.");
            }

            if (wifiDirectLink is null)
            {
                await bleCoordinator.SendFastChannelAnswerAsync(offer.SessionId, false, "wifi_direct_not_ready");
                throw new InvalidOperationException("O enlace Wi-Fi Direct ainda não está formado.");
            }

            if (offer.Role != Veyro.Protocol.FastChannelRole.GroupOwner)
            {
                await bleCoordinator.SendFastChannelAnswerAsync(offer.SessionId, false, "unsupported_role");
                throw new InvalidOperationException("A função solicitada para o canal rápido não é suportada.");
            }

            await bleCoordinator.SendFastChannelAnswerAsync(offer.SessionId, true);
            await ConnectFastChannelAsync(wifiDirectLink, offer, trustedDevice, lifetime.Token);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Oferta do canal rápido rejeitada", exception));
        }
    }

    private void BleCoordinator_FastChannelAnswerReceived(object? sender, FastChannelAnswerEventArgs args)
    {
        if (!args.Answer.Accepted)
        {
            listener?.Stop();
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs($"Canal rápido recusado: {args.Answer.Reason}"));
        }
    }

    private async Task AcceptFastChannelAsync(
        TcpListener activeListener,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tcpClient = await activeListener.AcceptTcpClientAsync(cancellationToken);
            var channel = await SecureFastChannel.AcceptAsync(
                tcpClient,
                localIdentity.DeviceId,
                localCertificate,
                trustStore.FindActive,
                cancellationToken);
            await channel.PerformProtocolHandshakeAsync(sessionId, cancellationToken);
            await channel.AcceptResumeAsync(resumeRegistry, cancellationToken);
            RegisterChannel(channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Falha ao aceitar o socket seguro", exception));
        }
    }

    private async Task ConnectFastChannelAsync(
        WifiDirectPeerConnection connection,
        Veyro.Protocol.FastChannelOffer offer,
        TrustedDevice trustedDevice,
        CancellationToken cancellationToken)
    {
        var localAddress = ParseDirectAddress(connection.LocalAddress);
        var remoteAddress = ParseDirectAddress(connection.RemoteAddress);
        var tcpClient = new TcpClient(remoteAddress.AddressFamily);
        tcpClient.Client.Bind(new IPEndPoint(localAddress, 0));
        try
        {
            await tcpClient.ConnectAsync(remoteAddress, checked((int)offer.TcpPort), cancellationToken);
            var channel = await SecureFastChannel.ConnectAsync(
                tcpClient,
                localIdentity.DeviceId,
                localCertificate,
                trustedDevice,
                cancellationToken);
            await channel.PerformProtocolHandshakeAsync(offer.SessionId, cancellationToken);
            await channel.RequestResumeAsync(
                offer.SessionId,
                offer.ResumeToken.ToByteArray(),
                cancellationToken: cancellationToken);
            RegisterChannel(channel);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private void RegisterChannel(SecureFastChannel channel)
    {
        if (!sessions.TryAdd(channel.RemoteDeviceId, channel))
        {
            _ = channel.DisposeAsync();
            throw new InvalidOperationException("Já existe um canal rápido para este dispositivo.");
        }

        trustStore.MarkSeen(channel.RemoteDeviceId);
        StatusChanged?.Invoke(
            this,
            new FastChannelStatusEventArgs($"Canal seguro ativo com {channel.RemoteDeviceId}"));
        _ = RunChannelAsync(channel, lifetime.Token);
    }

    private async Task RunChannelAsync(SecureFastChannel channel, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await channel.RunAsync(KeepAliveInterval, ConnectionTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            sessions.TryRemove(channel.RemoteDeviceId, out _);
            await channel.DisposeAsync();
        }

        if (failure is not null)
        {
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs(
                    "Canal rápido interrompido; retomada disponível por cinco minutos",
                    failure));
        }
    }

    private void WifiDirectManager_StatusChanged(object? sender, WifiDirectStatusEventArgs args) =>
        StatusChanged?.Invoke(this, new FastChannelStatusEventArgs(args.Message, args.Error));

    private static IPAddress ParseDirectAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("O Wi-Fi Direct forneceu um endereço inválido.");
        }

        return address;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        wifiDirectManager.PeerConnected -= WifiDirectManager_PeerConnected;
        wifiDirectManager.StatusChanged -= WifiDirectManager_StatusChanged;
        bleCoordinator.FastChannelOfferReceived -= BleCoordinator_FastChannelOfferReceived;
        bleCoordinator.FastChannelAnswerReceived -= BleCoordinator_FastChannelAnswerReceived;
        await lifetime.CancelAsync();
        listener?.Stop();
        wifiDirectManager.Dispose();
        foreach (var channel in sessions.Values)
        {
            await channel.DisposeAsync();
        }

        sessions.Clear();
        localCertificate.Dispose();
        lifetime.Dispose();
    }
}
