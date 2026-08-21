using System.Security.Cryptography;
using Veyro.Desktop.Bluetooth;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Pairing;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Pairing;

public sealed class BlePairingCoordinator : IDisposable
{
    private readonly LocalIdentity localIdentity;
    private readonly LocalIdentityKey localIdentityKey;
    private readonly VeyroCapability capabilities;
    private readonly TrustStore trustStore;
    private readonly BleGattControlServer server = new();
    private readonly BleGattControlClient client = new();
    private readonly SemaphoreSlim packetGate = new(1, 1);
    private PairingSession? pairingSession;
    private Func<byte[], Task>? reply;
    private byte[]? reconnectChallenge;
    private bool disposed;

    public BlePairingCoordinator(
        LocalIdentity localIdentity,
        LocalIdentityKey localIdentityKey,
        VeyroCapability capabilities,
        TrustStore trustStore)
    {
        this.localIdentity = localIdentity;
        this.localIdentityKey = localIdentityKey;
        this.capabilities = capabilities;
        this.trustStore = trustStore;
        server.PacketReceived += Server_PacketReceived;
        client.PacketReceived += Client_PacketReceived;
    }

    public event EventHandler<PairingPinEventArgs>? PinAvailable;

    public event EventHandler<PairingStatusEventArgs>? StatusChanged;

    public event EventHandler? TrustChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await server.StartAsync();
        StatusChanged?.Invoke(this, new PairingStatusEventArgs("Canal de pareamento BLE disponível"));
    }

    public async Task BeginPairingAsync(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ObjectDisposedException.ThrowIf(disposed, this);

        await packetGate.WaitAsync();
        try
        {
            ResetSession();
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Conectando ao dispositivo próximo…"));
            await client.ConnectAsync(device.BluetoothAddress);
            reply = client.SendAsync;

            reconnectChallenge = TrustedPeerAuthenticator.CreateChallenge();
            await reply(PairingMessageCodec.EncodeReconnectChallenge(localIdentity.DeviceId, reconnectChallenge));

            pairingSession = PairingSession.Create(localIdentity, localIdentityKey, capabilities);
            await reply(PairingMessageCodec.EncodeHello(pairingSession.LocalHello));
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Solicitação de pareamento enviada"));
        }
        catch (Exception exception)
        {
            ResetSession();
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Falha ao iniciar o pareamento", exception));
            throw;
        }
        finally
        {
            packetGate.Release();
        }
    }

    public async Task ConfirmPinAsync(bool accepted)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await packetGate.WaitAsync();
        try
        {
            if (pairingSession is null || reply is null)
            {
                throw new InvalidOperationException("Não existe pareamento aguardando confirmação.");
            }

            await reply(PairingMessageCodec.EncodeConfirmation(pairingSession.CreateConfirmation(accepted)));
            if (!accepted)
            {
                StatusChanged?.Invoke(this, new PairingStatusEventArgs("Pareamento recusado neste computador"));
                ResetSession();
                return;
            }

            CompletePairingIfReady();
        }
        finally
        {
            packetGate.Release();
        }
    }

    public bool Revoke(string deviceId)
    {
        var revoked = trustStore.Revoke(deviceId);
        if (revoked)
        {
            TrustChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Confiança revogada"));
        }

        return revoked;
    }

    private async void Server_PacketReceived(object? sender, BleControlPacketEventArgs args) =>
        await ProcessPacketSafelyAsync(args.Packet, server.NotifyAsync);

    private async void Client_PacketReceived(object? sender, BleControlPacketEventArgs args) =>
        await ProcessPacketSafelyAsync(args.Packet, client.SendAsync);

    private async Task ProcessPacketSafelyAsync(byte[] bytes, Func<byte[], Task> response)
    {
        await packetGate.WaitAsync();
        try
        {
            var packet = PairingMessageCodec.Decode(bytes);
            switch (packet.BodyCase)
            {
                case Veyro.Protocol.BleControlPacket.BodyOneofCase.PairingHello:
                    await ProcessHelloAsync(PairingMessageCodec.ToCore(packet.PairingHello), response);
                    break;
                case Veyro.Protocol.BleControlPacket.BodyOneofCase.PairingConfirmation:
                    ProcessConfirmation(PairingMessageCodec.ToCore(packet.PairingConfirmation));
                    break;
                case Veyro.Protocol.BleControlPacket.BodyOneofCase.ReconnectChallenge:
                    await ProcessReconnectChallengeAsync(packet.ReconnectChallenge, response);
                    break;
                case Veyro.Protocol.BleControlPacket.BodyOneofCase.ReconnectProof:
                    ProcessReconnectProof(packet.ReconnectProof);
                    break;
                case Veyro.Protocol.BleControlPacket.BodyOneofCase.None:
                default:
                    throw new PairingProtocolException("Unsupported BLE control packet.");
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Mensagem BLE rejeitada", exception));
        }
        finally
        {
            packetGate.Release();
        }
    }

    private async Task ProcessHelloAsync(PairingHello remoteHello, Func<byte[], Task> response)
    {
        reply = response;
        var sendLocalHello = pairingSession is null ||
            !string.Equals(pairingSession.LocalHello.PairingId, remoteHello.PairingId, StringComparison.Ordinal);
        if (sendLocalHello)
        {
            ResetSession();
            reply = response;
            pairingSession = PairingSession.Create(
                localIdentity,
                localIdentityKey,
                capabilities,
                remoteHello.PairingId);
        }

        var verification = pairingSession!.AcceptRemoteHello(remoteHello);
        if (sendLocalHello)
        {
            await response(PairingMessageCodec.EncodeHello(pairingSession.LocalHello));
        }

        PinAvailable?.Invoke(this, new PairingPinEventArgs(verification));
        StatusChanged?.Invoke(this, new PairingStatusEventArgs("Confirme o mesmo PIN nos dois dispositivos"));
    }

    private void ProcessConfirmation(PairingConfirmation confirmation)
    {
        if (pairingSession is null)
        {
            throw new PairingProtocolException("A confirmation arrived without an active pairing session.");
        }

        pairingSession.AcceptRemoteConfirmation(confirmation);
        if (!confirmation.Accepted)
        {
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Pareamento recusado pelo outro dispositivo"));
            ResetSession();
            return;
        }

        CompletePairingIfReady();
    }

    private async Task ProcessReconnectChallengeAsync(
        Veyro.Protocol.ReconnectChallenge challenge,
        Func<byte[], Task> response)
    {
        if (challenge.Challenge.Length != 32)
        {
            throw new PairingProtocolException("The reconnect challenge has an invalid size.");
        }

        var challengeBytes = challenge.Challenge.ToByteArray();
        var signature = TrustedPeerAuthenticator.Sign(localIdentityKey, localIdentity.DeviceId, challengeBytes);
        await response(PairingMessageCodec.EncodeReconnectProof(localIdentity.DeviceId, challengeBytes, signature));
    }

    private void ProcessReconnectProof(Veyro.Protocol.ReconnectProof proof)
    {
        if (reconnectChallenge is null ||
            !CryptographicOperations.FixedTimeEquals(reconnectChallenge, proof.Challenge.Span))
        {
            throw new PairingProtocolException("The reconnect proof does not match the active challenge.");
        }

        var trustedDevice = trustStore.FindActive(proof.DeviceId);
        if (trustedDevice is null ||
            !TrustedPeerAuthenticator.Verify(trustedDevice, reconnectChallenge, proof.Signature.Span))
        {
            StatusChanged?.Invoke(this, new PairingStatusEventArgs("Dispositivo ainda não confiável; confirme o PIN"));
            return;
        }

        trustStore.MarkSeen(trustedDevice.DeviceId);
        TrustChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, new PairingStatusEventArgs($"{trustedDevice.DisplayName} autenticado novamente"));
    }

    private void CompletePairingIfReady()
    {
        if (pairingSession?.IsMutuallyConfirmed != true)
        {
            return;
        }

        var trustedDevice = pairingSession.CreateTrustedDevice();
        trustStore.Trust(trustedDevice);
        TrustChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, new PairingStatusEventArgs($"{trustedDevice.DisplayName} adicionado ao Trust Hub"));
        ResetSession();
    }

    private void ResetSession()
    {
        pairingSession?.Dispose();
        pairingSession = null;
        reply = null;
        if (reconnectChallenge is not null)
        {
            CryptographicOperations.ZeroMemory(reconnectChallenge);
            reconnectChallenge = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        server.PacketReceived -= Server_PacketReceived;
        client.PacketReceived -= Client_PacketReceived;
        ResetSession();
        client.Dispose();
        server.Dispose();
        packetGate.Dispose();
    }
}
