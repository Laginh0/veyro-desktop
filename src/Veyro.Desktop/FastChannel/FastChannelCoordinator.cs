using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Google.Protobuf;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Groups;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Protocol;
using Veyro.Desktop.Core.Routing;
using Veyro.Desktop.Core.Security;
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
    private readonly FastChannelResumeRegistry resumeRegistry;
    private readonly EnvelopeDeduplicator deduplicator = new();
    private readonly ConcurrentDictionary<string, SecureFastChannel> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> pendingSessionIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly X509Certificate2 localCertificate;
    private readonly GroupStateManager groupState;
    private readonly LogicalRouter router;
    private WifiDirectPeerConnection? wifiDirectLink;
    private TcpListener? listener;
    private IPAddress? listenerAddress;
    private long outboundSequence;
    private bool disposed;

    public FastChannelCoordinator(
        LocalIdentity localIdentity,
        LocalIdentityKey localIdentityKey,
        TrustStore trustStore,
        BlePairingCoordinator bleCoordinator,
        WifiDirectManager wifiDirectManager,
        VeyroCapability localCapabilities,
        FastChannelResumeRegistry resumeRegistry)
    {
        this.localIdentity = localIdentity;
        this.localIdentityKey = localIdentityKey;
        this.trustStore = trustStore;
        this.bleCoordinator = bleCoordinator;
        this.wifiDirectManager = wifiDirectManager;
        this.resumeRegistry = resumeRegistry;
        localCertificate = VeyroTlsIdentity.CreateCertificate(localIdentity, localIdentityKey);
        var onExternalPower = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
            System.Windows.Forms.PowerLineStatus.Online;
        var localMember = new GroupMemberState(
            localIdentity.DeviceId,
            localIdentity.DisplayName,
            localCapabilities,
            CoordinatorEligible:
                localCapabilities.HasFlag(VeyroCapability.WifiDirectData) &&
                localCapabilities.HasFlag(VeyroCapability.MultiDeviceRouting),
            OnExternalPower: onExternalPower,
            BatteryPercent: GetBatteryPercent(),
            StabilitySeconds: 0,
            MaximumDirectPeers: 3,
            IsAvailable: true,
            LastSeenAtUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        groupState = new GroupStateManager(localMember);
        router = new LogicalRouter(localIdentity.DeviceId, deduplicator, trustStore.FindActive);
        groupState.StateChanged += GroupState_StateChanged;
        wifiDirectManager.PeerConnected += WifiDirectManager_PeerConnected;
        wifiDirectManager.StatusChanged += WifiDirectManager_StatusChanged;
        bleCoordinator.FastChannelOfferReceived += BleCoordinator_FastChannelOfferReceived;
        bleCoordinator.FastChannelAnswerReceived += BleCoordinator_FastChannelAnswerReceived;
    }

    public event EventHandler<FastChannelStatusEventArgs>? StatusChanged;

    public event EventHandler<GroupStateChangedEventArgs>? GroupStateChanged;

    public event EventHandler<RoutedEnvelopeEventArgs>? EnvelopeReceived;

    public int ActiveSessionCount => sessions.Count;

    public string CoordinatorDeviceId => groupState.CoordinatorDeviceId;

    public ulong GroupEpoch => groupState.Epoch;

    public IReadOnlyList<GroupMemberState> GroupMembers => groupState.Snapshot();

    public void InvalidateResumeState(string deviceId)
    {
        resumeRegistry.RemoveDevice(deviceId);
        pendingSessionIds.TryRemove(deviceId, out _);
        if (sessions.TryGetValue(deviceId, out var channel))
        {
            _ = channel.DisposeAsync();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        wifiDirectManager.Start();
    }

    public void RecoverAfterSystemResume()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        resumeRegistry.RemoveExpired(DateTimeOffset.UtcNow);
        wifiDirectManager.RebuildGroup();
        StatusChanged?.Invoke(
            this,
            new FastChannelStatusEventArgs("Rádios reativados após a retomada do Windows"));
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

            EnsureListener(localAddress);
            var port = checked((ushort)((IPEndPoint)listener!.LocalEndpoint).Port);
            var resumeState = resumeRegistry.FindActiveForDevice(trustedDevice.DeviceId, DateTimeOffset.UtcNow)
                ?? resumeRegistry.Create(trustedDevice.DeviceId);
            var offer = FastChannelOfferSigner.Create(
                localIdentity,
                localIdentityKey,
                Veyro.Protocol.FastChannelRole.GroupOwner,
                port,
                resumeState);
            pendingSessionIds[trustedDevice.DeviceId] = offer.SessionId;
            await bleCoordinator.SendFastChannelOfferAsync(offer);
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Oferta do canal rápido enviada pelo BLE"));
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

    public async Task SendApplicationEnvelopeAsync(
        ReadOnlyMemory<byte> encryptedPayload,
        IReadOnlyCollection<string>? destinationDeviceIds = null,
        bool authorizedBroadcast = false,
        CancellationToken cancellationToken = default)
    {
        if (encryptedPayload.IsEmpty)
        {
            throw new ArgumentException("The application payload cannot be empty.", nameof(encryptedPayload));
        }

        var envelope = CreateEnvelope(
            encryptedPayload,
            Veyro.Protocol.TransportPayloadType.ApplicationMessage,
            destinationDeviceIds,
            authorizedBroadcast);
        await SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async Task SendApplicationMessageAsync(
        Veyro.Protocol.VeyroMessage message,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationDeviceIds);
        if (!string.Equals(
                message.ProtocolVersion,
                ProtocolContract.AndroidFeatureProtocolVersion,
                StringComparison.Ordinal) ||
            message.PayloadCase == Veyro.Protocol.VeyroMessage.PayloadOneofCase.None)
        {
            throw new InvalidDataException("A mensagem de aplicação não pertence ao contrato Veyro atual.");
        }

        var destinations = destinationDeviceIds
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (destinations.Length == 0)
        {
            throw new ArgumentException("Selecione ao menos um dispositivo de destino.", nameof(destinationDeviceIds));
        }

        var recipients = destinations
            .Select(deviceId => trustStore.FindActive(deviceId)
                ?? throw new InvalidOperationException($"O destino {deviceId} não está ativo no Trust Hub."))
            .ToArray();
        var encryptedPayload = ApplicationPayloadCipher.Encrypt(
            message.ToByteArray(),
            localIdentity.DeviceId,
            recipients);
        await SendApplicationEnvelopeAsync(
            encryptedPayload,
            destinations,
            authorizedBroadcast: false,
            cancellationToken);
    }

    private void EnsureListener(IPAddress localAddress)
    {
        if (listener is not null && Equals(listenerAddress, localAddress))
        {
            return;
        }

        listener?.Stop();
        listener = new TcpListener(localAddress, 0);
        listener.Start(backlog: 8);
        listenerAddress = localAddress;
        _ = AcceptFastChannelsAsync(listener, lifetime.Token);
    }

    private async Task AcceptFastChannelsAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await activeListener.AcceptTcpClientAsync(cancellationToken);
                _ = AuthenticateAcceptedChannelAsync(tcpClient, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || activeListener != listener)
            {
                break;
            }
            catch (SocketException) when (activeListener != listener)
            {
                break;
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke(
                    this,
                    new FastChannelStatusEventArgs("Falha ao aceitar um socket do grupo", exception));
            }
        }
    }

    private async Task AuthenticateAcceptedChannelAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        try
        {
            var channel = await SecureFastChannel.AcceptAsync(
                tcpClient,
                localIdentity.DeviceId,
                localCertificate,
                trustStore.FindActive,
                cancellationToken);
            if (!pendingSessionIds.TryGetValue(channel.RemoteDeviceId, out var sessionId))
            {
                await channel.DisposeAsync();
                throw new InvalidOperationException("A sessão TLS não possui uma oferta BLE correspondente.");
            }

            await channel.PerformProtocolHandshakeAsync(sessionId, cancellationToken);
            await channel.AcceptResumeAsync(resumeRegistry, cancellationToken);
            RegisterChannel(channel);
        }
        catch (Exception exception)
        {
            tcpClient.Dispose();
            StatusChanged?.Invoke(this, new FastChannelStatusEventArgs("Falha ao autenticar membro do grupo", exception));
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
            RegisterChannel(channel, remoteIsCoordinator: true);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private void RegisterChannel(SecureFastChannel channel, bool remoteIsCoordinator = false)
    {
        if (!sessions.TryAdd(channel.RemoteDeviceId, channel))
        {
            _ = channel.DisposeAsync();
            throw new InvalidOperationException("Já existe um canal rápido para este dispositivo.");
        }

        channel.PacketReceived += Channel_PacketReceived;
        pendingSessionIds.TryRemove(channel.RemoteDeviceId, out _);
        trustStore.MarkSeen(channel.RemoteDeviceId);
        var trustedDevice = trustStore.FindActive(channel.RemoteDeviceId)
            ?? throw new InvalidOperationException("O membro autenticado não está ativo no Trust Hub.");
        groupState.Upsert(
            new GroupMemberState(
                trustedDevice.DeviceId,
                trustedDevice.DisplayName,
                trustedDevice.Capabilities,
                CoordinatorEligible:
                    trustedDevice.Capabilities.HasFlag(VeyroCapability.WifiDirectData) &&
                    trustedDevice.Capabilities.HasFlag(VeyroCapability.MultiDeviceRouting),
                OnExternalPower: false,
                BatteryPercent: 0,
                StabilitySeconds: 0,
                MaximumDirectPeers: 3,
                IsAvailable: true,
                LastSeenAtUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            "member_connected");
        if (remoteIsCoordinator)
        {
            groupState.AdoptInitialCoordinator(channel.RemoteDeviceId);
        }
        StatusChanged?.Invoke(
            this,
            new FastChannelStatusEventArgs($"Canal seguro ativo com {channel.RemoteDeviceId}"));
        _ = RunChannelAsync(channel, lifetime.Token);
        _ = BroadcastGroupStateIfCoordinatorAsync("member_connected", lifetime.Token);
        _ = BroadcastAndroidTopologyAsync(lifetime.Token);
    }

    private async void Channel_PacketReceived(object? sender, FastChannelPacketEventArgs args)
    {
        if (sender is not SecureFastChannel ingress ||
            args.Packet.BodyCase != Veyro.Protocol.FastChannelPacket.BodyOneofCase.TransportEnvelope)
        {
            return;
        }

        try
        {
            var envelope = args.Packet.TransportEnvelope;
            var decision = router.RouteIncoming(
                envelope,
                ingress.RemoteDeviceId,
                sessions.Keys.ToArray(),
                groupState.LocalIsCoordinator,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (decision.IsRejected)
            {
                StatusChanged?.Invoke(
                    this,
                    new FastChannelStatusEventArgs($"Envelope descartado: {decision.RejectionReason}"));
                return;
            }

            if (decision.DeliverLocally)
            {
                if (envelope.PayloadType == Veyro.Protocol.TransportPayloadType.ControlMessage)
                {
                    ApplyGroupControl(envelope);
                }
                else
                {
                    var plaintext = ApplicationPayloadCipher.Decrypt(
                        envelope.EncryptedPayload.Span,
                        envelope.OriginDeviceId,
                        localIdentity.DeviceId,
                        localIdentityKey);
                    try
                    {
                        var message = Veyro.Protocol.VeyroMessage.Parser.ParseFrom(plaintext);
                        if (!string.Equals(
                                message.ProtocolVersion,
                                ProtocolContract.AndroidFeatureProtocolVersion,
                                StringComparison.Ordinal) ||
                            message.PayloadCase == Veyro.Protocol.VeyroMessage.PayloadOneofCase.None)
                        {
                            throw new InvalidDataException("A mensagem de aplicação recebida é incompatível.");
                        }

                        EnvelopeReceived?.Invoke(this, new RoutedEnvelopeEventArgs(envelope, message));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
            }

            if (decision.ForwardEnvelope is not null)
            {
                await SendToTargetsAsync(
                    decision.ForwardEnvelope,
                    decision.ForwardTargets,
                    lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs("Falha ao processar envelope roteado", exception));
        }
    }

    private void ApplyGroupControl(Veyro.Protocol.TransportEnvelope envelope)
    {
        var control = GroupControlCodec.Decode(envelope.EncryptedPayload.Span);
        if (control.Kind is not GroupControlKind.MembershipSnapshot and
            not GroupControlKind.CoordinatorCommitted)
        {
            throw new InvalidDataException("O tipo de controle de grupo ainda não é suportado.");
        }

        if (!string.Equals(control.InitiatorDeviceId, envelope.OriginDeviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A origem do controle de grupo não corresponde à assinatura.");
        }

        if (control.Kind is GroupControlKind.MembershipSnapshot or GroupControlKind.CoordinatorCommitted &&
            !string.Equals(control.CoordinatorDeviceId, envelope.OriginDeviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Somente o coordenador pode publicar o estado do grupo.");
        }

        if (!groupState.Apply(control))
        {
            throw new InvalidDataException("O estado de grupo recebido é antigo ou inconsistente.");
        }
    }

    private Veyro.Protocol.TransportEnvelope CreateEnvelope(
        ReadOnlyMemory<byte> payload,
        Veyro.Protocol.TransportPayloadType payloadType,
        IReadOnlyCollection<string>? destinationDeviceIds,
        bool authorizedBroadcast)
    {
        var destinations = destinationDeviceIds?
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (authorizedBroadcast == (destinations.Length > 0))
        {
            throw new ArgumentException(
                "Escolha um broadcast autorizado ou ao menos um destino, nunca os dois.",
                nameof(destinationDeviceIds));
        }

        var now = DateTimeOffset.UtcNow;
        var envelope = new Veyro.Protocol.TransportEnvelope
        {
            ProtocolMajor = ProtocolContract.TransportMajor,
            ProtocolMinor = ProtocolContract.TransportMinor,
            MessageId = Guid.NewGuid().ToString("D"),
            OriginDeviceId = localIdentity.DeviceId,
            AuthorizedBroadcast = authorizedBroadcast,
            PayloadType = payloadType,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = now.AddMinutes(2).ToUnixTimeMilliseconds(),
            RemainingHops = ProtocolContract.DefaultHopLimit,
            SequenceNumber = checked((ulong)Interlocked.Increment(ref outboundSequence)),
            EncryptedPayload = ByteString.CopyFrom(payload.Span)
        };
        envelope.DestinationDeviceIds.Add(destinations);
        TransportEnvelopeSigner.Sign(envelope, localIdentityKey);
        deduplicator.TryRemember(
            envelope.MessageId,
            envelope.ExpiresAtUnixMs,
            envelope.CreatedAtUnixMs);
        return envelope;
    }

    private async Task SendEnvelopeAsync(
        Veyro.Protocol.TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var targets = router.PlanOutboundTargets(
            envelope,
            sessions.Keys.ToArray(),
            groupState.LocalIsCoordinator,
            groupState.CoordinatorDeviceId);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Nenhum canal seguro alcança o destino solicitado.");
        }

        await SendToTargetsAsync(envelope, targets, cancellationToken);
    }

    private async Task SendToTargetsAsync(
        Veyro.Protocol.TransportEnvelope envelope,
        IReadOnlyCollection<string> targetDeviceIds,
        CancellationToken cancellationToken)
    {
        foreach (var targetDeviceId in targetDeviceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessions.TryGetValue(targetDeviceId, out var channel))
            {
                continue;
            }

            await channel.SendAsync(
                new Veyro.Protocol.FastChannelPacket { TransportEnvelope = envelope },
                cancellationToken);
        }
    }

    private async Task BroadcastGroupStateIfCoordinatorAsync(
        string reason,
        CancellationToken cancellationToken,
        GroupControlKind kind = GroupControlKind.MembershipSnapshot)
    {
        if (!groupState.LocalIsCoordinator || sessions.IsEmpty)
        {
            return;
        }

        try
        {
            var control = groupState.CreateControlMessage(kind, reason);
            var envelope = CreateEnvelope(
                GroupControlCodec.Encode(control),
                Veyro.Protocol.TransportPayloadType.ControlMessage,
                destinationDeviceIds: null,
                authorizedBroadcast: true);
            await SendEnvelopeAsync(envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs("Falha ao sincronizar o estado do grupo", exception));
        }
    }

    private async Task BroadcastAndroidTopologyAsync(CancellationToken cancellationToken)
    {
        if (!groupState.LocalIsCoordinator || sessions.IsEmpty)
        {
            return;
        }

        try
        {
            var topology = new Veyro.Protocol.GroupTopologyEvent
            {
                Epoch = groupState.Epoch,
                CoordinatorDeviceId = localIdentity.DeviceId
            };
            foreach (var member in groupState.Snapshot().Where(member => member.IsAvailable))
            {
                byte[] publicKey;
                if (string.Equals(member.DeviceId, localIdentity.DeviceId, StringComparison.Ordinal))
                {
                    publicKey = localIdentityKey.PublicKeySpki;
                }
                else
                {
                    var trusted = trustStore.FindActive(member.DeviceId);
                    if (trusted is null)
                    {
                        continue;
                    }

                    publicKey = Convert.FromBase64String(trusted.IdentityPublicKeyBase64);
                }

                topology.Members.Add(new Veyro.Protocol.GroupTopologyMember
                {
                    DeviceId = member.DeviceId,
                    DisplayName = member.DisplayName,
                    IdentityPublicKeySpki = ByteString.CopyFrom(publicKey),
                    IsCoordinator = string.Equals(
                        member.DeviceId,
                        groupState.CoordinatorDeviceId,
                        StringComparison.Ordinal),
                    IsAvailable = member.IsAvailable
                });
            }

            var message = new Veyro.Protocol.VeyroMessage
            {
                ProtocolVersion = ProtocolContract.AndroidFeatureProtocolVersion,
                GroupTopologyEvent = topology
            };
            await SendApplicationMessageAsync(message, sessions.Keys.ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs("Falha ao anunciar a topologia estrela", exception));
        }
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
            channel.PacketReceived -= Channel_PacketReceived;
            sessions.TryRemove(channel.RemoteDeviceId, out _);
            pendingSessionIds.TryRemove(channel.RemoteDeviceId, out _);
            var disconnectedCoordinator = string.Equals(
                groupState.CoordinatorDeviceId,
                channel.RemoteDeviceId,
                StringComparison.Ordinal);
            groupState.MarkUnavailable(
                channel.RemoteDeviceId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await channel.DisposeAsync();
            if (groupState.LocalIsCoordinator && !lifetime.IsCancellationRequested)
            {
                await BroadcastGroupStateIfCoordinatorAsync(
                    disconnectedCoordinator ? "coordinator_disconnected" : "member_disconnected",
                    lifetime.Token,
                    disconnectedCoordinator
                        ? GroupControlKind.CoordinatorCommitted
                        : GroupControlKind.MembershipSnapshot);
                await BroadcastAndroidTopologyAsync(lifetime.Token);
                if (disconnectedCoordinator)
                {
                    wifiDirectManager.RebuildGroup();
                }
            }
        }

        if (failure is not null)
        {
            StatusChanged?.Invoke(
                this,
                new FastChannelStatusEventArgs(
                    "Canal rápido interrompido; retomada protegida disponível por 24 horas",
                    failure));
        }
    }

    private void WifiDirectManager_StatusChanged(object? sender, WifiDirectStatusEventArgs args) =>
        StatusChanged?.Invoke(this, new FastChannelStatusEventArgs(args.Message, args.Error));

    private void GroupState_StateChanged(object? sender, GroupStateChangedEventArgs args) =>
        GroupStateChanged?.Invoke(this, args);

    private static byte GetBatteryPercent()
    {
        var percent = System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent;
        return percent < 0
            ? (byte)0
            : checked((byte)Math.Clamp((int)Math.Round(percent * 100), 0, 100));
    }

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
        groupState.StateChanged -= GroupState_StateChanged;
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
