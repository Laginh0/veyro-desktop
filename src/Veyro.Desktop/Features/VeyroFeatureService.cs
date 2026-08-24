using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using Veyro.Desktop.Core.Features;
using Veyro.Desktop.Core.Protocol;
using Veyro.Desktop.Core.Trust;
using Veyro.Desktop.FastChannel;
using Veyro.Protocol;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Veyro.Desktop.Features;

public sealed class VeyroFeatureService : IAsyncDisposable
{
    private const int FileChunkSize = 128 * 1024;
    private const long MaximumIncomingFileSize = 4L * 1024 * 1024 * 1024;
    private const byte VolumeUpKey = 0xAF;
    private const byte VolumeDownKey = 0xAE;
    private const uint KeyUp = 0x0002;
    private readonly FastChannelCoordinator channelCoordinator;
    private readonly TrustStore trustStore;
    private readonly FeaturePermissionStore permissionStore;
    private readonly SharedFolderStore sharedFolderStore;
    private readonly WindowsInputController inputController = new();
    private readonly string incomingDirectory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> outgoingFileOffers = new();
    private readonly ConcurrentDictionary<string, IncomingFileState> incomingFiles = new();
    private readonly ConcurrentDictionary<string, long> pendingPings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InputRateWindow> inputRateWindows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public VeyroFeatureService(
        FastChannelCoordinator channelCoordinator,
        TrustStore trustStore,
        FeaturePermissionStore permissionStore,
        SharedFolderStore sharedFolderStore,
        string incomingDirectory)
    {
        this.channelCoordinator = channelCoordinator;
        this.trustStore = trustStore;
        this.permissionStore = permissionStore;
        this.sharedFolderStore = sharedFolderStore;
        this.incomingDirectory = incomingDirectory;
        channelCoordinator.EnvelopeReceived += ChannelCoordinator_EnvelopeReceived;
    }

    public event EventHandler<FeatureStatusEventArgs>? StatusChanged;

    public event EventHandler<FeatureAuthorizationEventArgs>? AuthorizationRequested;

    public event EventHandler<ClipboardReceivedEventArgs>? ClipboardReceived;

    public event EventHandler<VeyroNotificationEventArgs>? NotificationReceived;

    public event EventHandler<RemoteDeviceStateEventArgs>? RemoteDeviceStateChanged;

    public event EventHandler<RemoteStylusEventArgs>? RemoteStylusReceived;

    public event EventHandler<RemoteFilesEventArgs>? RemoteFilesReceived;

    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgressChanged;

    public IReadOnlyList<SharedFolder> SharedFolders => sharedFolderStore.Snapshot();

    public FeatureAccessPolicy GetPolicy(string deviceId, VeyroFeature feature) =>
        permissionStore.GetPolicy(deviceId, feature);

    public void SetPolicy(string deviceId, VeyroFeature feature, FeatureAccessPolicy policy) =>
        permissionStore.SetPolicy(deviceId, feature, policy);

    public void RemovePermissions(string deviceId) => permissionStore.RemoveDevice(deviceId);

    public SharedFolder AddSharedFolder(string directoryPath) => sharedFolderStore.Add(directoryPath);

    public bool RemoveSharedFolder(string folderId) => sharedFolderStore.Remove(folderId);

    public async Task SendFilesAsync(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        foreach (var filePath in filePaths)
        {
            foreach (var destinationDeviceId in destinationDeviceIds)
            {
                await SendFileToDeviceAsync(filePath, destinationDeviceId, cancellationToken);
            }
        }
    }

    public Task SendClipboardAsync(
        string text,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64 * 1024)
        {
            throw new ArgumentException("O texto do clipboard está vazio ou excede 64 KiB.", nameof(text));
        }

        return SendAsync(
            new VeyroMessage
            {
                ClipboardSyncEvent = new ClipboardSyncEvent
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    Text = text,
                    EventTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task SendLinkAsync(
        string link,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateWebUri(link);
        return SendAsync(
            new VeyroMessage
            {
                UrlShareEvent = new UrlShareEvent
                {
                    HyperlinkTarget = uri.AbsoluteUri,
                    RequiresImmediateFocus = false
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task SendBatteryStatusAsync(
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        var percentage = power.BatteryLifePercent < 0
            ? 0
            : Math.Clamp((int)Math.Round(power.BatteryLifePercent * 100), 0, 100);
        return SendAsync(
            new VeyroMessage
            {
                BatteryStatus = new BatteryStatus
                {
                    ChargePercentage = percentage,
                    IsPluggedIn = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                    PowerSourceType = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online
                        ? PowerSourceType.AcWallOutlet
                        : PowerSourceType.UnknownSource,
                    EventTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task SendConnectivityStatusAsync(
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .ToArray();
        var transport = activeInterfaces.Any(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            ? NetworkTransport.Wifi
            : activeInterfaces.Any(item => item.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                ? NetworkTransport.Ethernet
                : activeInterfaces.Length > 0
                    ? NetworkTransport.Other
                    : NetworkTransport.None;
        return SendAsync(
            new VeyroMessage
            {
                ConnectivityStatus = new ConnectivityStatus
                {
                    ActiveTransport = transport,
                    HasInternet = false,
                    IsMetered = false,
                    HasSignalStrength = false,
                    EventTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task PingAsync(string destinationDeviceId, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("D");
        pendingPings[requestId] = Stopwatch.GetTimestamp();
        return SendAsync(
            new VeyroMessage
            {
                PingEvent = new PingEvent
                {
                    RequestId = requestId,
                    Action = PingAction.PingRequest
                }
            },
            [destinationDeviceId],
            cancellationToken);
    }

    public Task SendMediaCommandAsync(
        MediaEventCategory command,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (command is not MediaEventCategory.CmdPlay and
            not MediaEventCategory.CmdPause and
            not MediaEventCategory.CmdNext and
            not MediaEventCategory.CmdPrev and
            not MediaEventCategory.CmdVolUp and
            not MediaEventCategory.CmdVolDown)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        return SendAsync(
            new VeyroMessage
            {
                MediaControlEvent = new MediaControlEvent { EventCategory = command }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task SendPresentationActionAsync(
        PresentationAction action,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (action == PresentationAction.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        return SendAsync(
            new VeyroMessage
            {
                PresentationEvent = new PresentationEvent
                {
                    Action = action,
                    ElapsedMillis = Environment.TickCount64
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task SendSafeCommandAsync(
        string command,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedOutgoingCommand(command))
        {
            throw new ArgumentException("A ação não pertence à lista segura do Veyro.", nameof(command));
        }

        return SendAsync(
            new VeyroMessage
            {
                CustomCommandEvent = new CustomCommandEvent
                {
                    CommandTrackingId = Guid.NewGuid().ToString("D"),
                    ExecutionTypeCategory = ExecutionTypeCategory.SystemUriActionCall,
                    EncodedCommandString = command,
                    AwaitOutputConfirmation = true
                }
            },
            destinationDeviceIds,
            cancellationToken);
    }

    public async Task SyncWindowsNotificationsAsync(
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var listener = UserNotificationListener.Current;
        var access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            throw new UnauthorizedAccessException("O Windows não autorizou o acesso às notificações.");
        }

        var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var notification in notifications.OrderByDescending(item => item.CreationTime).Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = binding?.GetTextElements().Select(item => item.Text).ToArray() ?? [];
            await SendAsync(
                new VeyroMessage
                {
                    NotificationSyncEvent = new NotificationSyncEvent
                    {
                        SyncAction = NotificationSyncAction.PostNew,
                        NotificationKey = notification.Id.ToString(),
                        PackageName = notification.AppInfo.AppUserModelId,
                        AppName = notification.AppInfo.DisplayInfo.DisplayName,
                        Title = text.FirstOrDefault() ?? notification.AppInfo.DisplayInfo.DisplayName,
                        TextBody = string.Join(Environment.NewLine, text.Skip(1)),
                        IsClearable = true
                    }
                },
                destinationDeviceIds,
                cancellationToken);
        }

        StatusChanged?.Invoke(
            this,
            new FeatureStatusEventArgs($"{Math.Min(notifications.Count, 20)} notificações sincronizadas"));
    }

    public Task SendRemoteInputAsync(
        RemoteInputEvent input,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SendAsync(
            new VeyroMessage { RemoteInputEvent = input },
            destinationDeviceIds,
            cancellationToken);
    }

    public Task RequestRemoteFilesAsync(
        string destinationDeviceId,
        string parentDocumentId = "",
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new VeyroMessage
            {
                RemoteFileEvent = new RemoteFileEvent
                {
                    RequestId = Guid.NewGuid().ToString("D"),
                    Action = RemoteFileAction.ListRequest,
                    ParentDocumentId = parentDocumentId
                }
            },
            [destinationDeviceId],
            cancellationToken);

    public Task RequestRemoteFileDownloadAsync(
        string destinationDeviceId,
        string documentId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new VeyroMessage
            {
                RemoteFileEvent = new RemoteFileEvent
                {
                    RequestId = Guid.NewGuid().ToString("D"),
                    Action = RemoteFileAction.DownloadRequest,
                    RequestedDocumentId = documentId
                }
            },
            [destinationDeviceId],
            cancellationToken);

    private async Task SendFileToDeviceAsync(
        string filePath,
        string destinationDeviceId,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length > MaximumIncomingFileSize)
        {
            throw new FileNotFoundException("O arquivo não existe ou excede o limite de 4 GiB.", filePath);
        }

        var transferId = Guid.NewGuid().ToString("D");
        var startedAt = Stopwatch.GetTimestamp();
        ReportFileTransfer(
            transferId,
            destinationDeviceId,
            file.Name,
            FileTransferDirection.Sending,
            FileTransferStage.Preparing,
            0,
            file.Length,
            startedAt);
        byte[] hash;
        await using (var hashStream = file.OpenRead())
        {
            hash = await SHA256.HashDataAsync(hashStream, cancellationToken);
        }

        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        outgoingFileOffers[OfferKey(transferId, destinationDeviceId)] = response;
        long sentBytes = 0;
        try
        {
            await SendFileEventAsync(
                new FileTransferEvent
                {
                    TransferId = transferId,
                    Action = FileTransferAction.FileOffer,
                    FileName = file.Name,
                    MimeType = InferMimeType(file.Extension),
                    SizeBytes = file.Length,
                    Sha256 = ByteString.CopyFrom(hash)
                },
                destinationDeviceId,
                cancellationToken);
            StatusChanged?.Invoke(this, new FeatureStatusEventArgs($"Aguardando aceite de {file.Name}"));
            ReportFileTransfer(
                transferId,
                destinationDeviceId,
                file.Name,
                FileTransferDirection.Sending,
                FileTransferStage.AwaitingAcceptance,
                0,
                file.Length,
                startedAt);
            if (!await response.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken))
            {
                throw new InvalidOperationException("O destino recusou o arquivo.");
            }

            await using var stream = file.OpenRead();
            var buffer = new byte[FileChunkSize];
            uint chunkIndex = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await SendFileEventAsync(
                    new FileTransferEvent
                    {
                        TransferId = transferId,
                        Action = FileTransferAction.FileChunk,
                        ChunkIndex = chunkIndex++,
                        ChunkData = ByteString.CopyFrom(buffer, 0, read)
                    },
                    destinationDeviceId,
                    cancellationToken);
                sentBytes += read;
                ReportFileTransfer(
                    transferId,
                    destinationDeviceId,
                    file.Name,
                    FileTransferDirection.Sending,
                    FileTransferStage.Transferring,
                    sentBytes,
                    file.Length,
                    startedAt);
            }

            await SendFileEventAsync(
                new FileTransferEvent
                {
                    TransferId = transferId,
                    Action = FileTransferAction.FileComplete,
                    Sha256 = ByteString.CopyFrom(hash)
                },
                destinationDeviceId,
                cancellationToken);
            StatusChanged?.Invoke(this, new FeatureStatusEventArgs($"Arquivo enviado: {file.Name}"));
            ReportFileTransfer(
                transferId,
                destinationDeviceId,
                file.Name,
                FileTransferDirection.Sending,
                FileTransferStage.Completed,
                file.Length,
                file.Length,
                startedAt);
        }
        catch (Exception exception)
        {
            try
            {
                await SendFileEventAsync(
                    new FileTransferEvent
                    {
                        TransferId = transferId,
                        Action = FileTransferAction.FileCancel
                    },
                    destinationDeviceId,
                    CancellationToken.None);
            }
            catch
            {
            }

            ReportFileTransfer(
                transferId,
                destinationDeviceId,
                file.Name,
                FileTransferDirection.Sending,
                exception is OperationCanceledException ? FileTransferStage.Cancelled : FileTransferStage.Failed,
                sentBytes,
                file.Length,
                startedAt,
                exception);

            throw;
        }
        finally
        {
            outgoingFileOffers.TryRemove(OfferKey(transferId, destinationDeviceId), out _);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private async void ChannelCoordinator_EnvelopeReceived(object? sender, RoutedEnvelopeEventArgs args)
    {
        try
        {
            var origin = trustStore.FindActive(args.Envelope.OriginDeviceId)
                ?? throw new InvalidOperationException("A origem da mensagem não está mais no Trust Hub.");
            switch (args.Message.PayloadCase)
            {
                case VeyroMessage.PayloadOneofCase.FileTransferEvent:
                    await HandleFileTransferAsync(origin, args.Message.FileTransferEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.ClipboardSyncEvent:
                    await HandleClipboardAsync(origin, args.Message.ClipboardSyncEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.UrlShareEvent:
                    await HandleLinkAsync(origin, args.Message.UrlShareEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.BatteryStatus:
                    RemoteDeviceStateChanged?.Invoke(
                        this,
                        new RemoteDeviceStateEventArgs(origin.DeviceId, args.Message.BatteryStatus, null, null));
                    break;
                case VeyroMessage.PayloadOneofCase.ConnectivityStatus:
                    RemoteDeviceStateChanged?.Invoke(
                        this,
                        new RemoteDeviceStateEventArgs(origin.DeviceId, null, args.Message.ConnectivityStatus, null));
                    break;
                case VeyroMessage.PayloadOneofCase.PingEvent:
                    await HandlePingAsync(origin, args.Message.PingEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.NotificationSyncEvent:
                    await HandleNotificationAsync(origin, args.Message.NotificationSyncEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.MediaControlEvent:
                    await HandleMediaControlAsync(origin, args.Message.MediaControlEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.CustomCommandEvent:
                    await HandleSecureCommandAsync(origin, args.Message.CustomCommandEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.PresentationEvent:
                    await HandlePresentationAsync(origin, args.Message.PresentationEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.RemoteInputEvent:
                    await HandleRemoteInputAsync(origin, args.Message.RemoteInputEvent, lifetime.Token);
                    break;
                case VeyroMessage.PayloadOneofCase.RemoteFileEvent:
                    await HandleRemoteFileAsync(origin, args.Message.RemoteFileEvent, lifetime.Token);
                    break;
                default:
                    StatusChanged?.Invoke(
                        this,
                        new FeatureStatusEventArgs($"Mensagem {args.Message.PayloadCase} recebida"));
                    break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, new FeatureStatusEventArgs("Falha ao aplicar recurso recebido", exception));
        }
    }

    private async Task HandleFileTransferAsync(
        TrustedDevice origin,
        FileTransferEvent fileEvent,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(fileEvent.TransferId, "D", out _))
        {
            throw new InvalidDataException("A transferência recebida não possui um ID válido.");
        }

        switch (fileEvent.Action)
        {
            case FileTransferAction.FileOffer:
                await HandleFileOfferAsync(origin, fileEvent, cancellationToken);
                break;
            case FileTransferAction.FileAccept:
            case FileTransferAction.FileReject:
                if (outgoingFileOffers.TryGetValue(
                        OfferKey(fileEvent.TransferId, origin.DeviceId),
                        out var response))
                {
                    response.TrySetResult(fileEvent.Action == FileTransferAction.FileAccept);
                }

                break;
            case FileTransferAction.FileChunk:
                await WriteIncomingChunkAsync(origin.DeviceId, fileEvent, cancellationToken);
                break;
            case FileTransferAction.FileComplete:
                await CompleteIncomingFileAsync(origin.DeviceId, fileEvent, cancellationToken);
                break;
            case FileTransferAction.FileCancel:
                await CancelIncomingFileAsync(origin.DeviceId, fileEvent.TransferId);
                break;
            default:
                throw new InvalidDataException("A ação de transferência não é suportada.");
        }
    }

    private async Task HandleFileOfferAsync(
        TrustedDevice origin,
        FileTransferEvent offer,
        CancellationToken cancellationToken)
    {
        var safeName = Path.GetFileName(offer.FileName);
        if (string.IsNullOrWhiteSpace(safeName) ||
            offer.SizeBytes < 0 ||
            offer.SizeBytes > MaximumIncomingFileSize ||
            offer.Sha256.Length != 32)
        {
            throw new InvalidDataException("A oferta de arquivo contém metadados inválidos.");
        }

        var accepted = await AuthorizeAsync(
            origin,
            VeyroFeature.Files,
            $"receber o arquivo {safeName} ({FormatSize(offer.SizeBytes)})",
            cancellationToken);
        if (!accepted)
        {
            await SendFileEventAsync(
                new FileTransferEvent
                {
                    TransferId = offer.TransferId,
                    Action = FileTransferAction.FileReject,
                    ResultMessage = "not_authorized"
                },
                origin.DeviceId,
                cancellationToken);
            return;
        }

        Directory.CreateDirectory(incomingDirectory);
        var finalPath = CreateUniquePath(incomingDirectory, safeName);
        var temporaryPath = finalPath + $".{offer.TransferId}.part";
        var state = new IncomingFileState(
            origin.DeviceId,
            offer.TransferId,
            finalPath,
            temporaryPath,
            offer.SizeBytes,
            offer.Sha256.ToByteArray(),
            new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileChunkSize, true),
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            Stopwatch.GetTimestamp());
        if (!incomingFiles.TryAdd(OfferKey(offer.TransferId, origin.DeviceId), state))
        {
            await state.DisposeAsync();
            throw new InvalidOperationException("A transferência já está em andamento.");
        }

        await SendFileEventAsync(
            new FileTransferEvent
            {
                TransferId = offer.TransferId,
                Action = FileTransferAction.FileAccept
            },
            origin.DeviceId,
            cancellationToken);
        StatusChanged?.Invoke(this, new FeatureStatusEventArgs($"Recebendo {safeName}"));
        ReportFileTransfer(
            offer.TransferId,
            origin.DeviceId,
            safeName,
            FileTransferDirection.Receiving,
            FileTransferStage.Transferring,
            0,
            offer.SizeBytes,
            state.StartedAt);
    }

    private async Task WriteIncomingChunkAsync(
        string originDeviceId,
        FileTransferEvent chunk,
        CancellationToken cancellationToken)
    {
        var key = OfferKey(chunk.TransferId, originDeviceId);
        if (!incomingFiles.TryGetValue(key, out var state) ||
            chunk.ChunkIndex != state.NextChunkIndex ||
            chunk.ChunkData.IsEmpty ||
            chunk.ChunkData.Length > FileChunkSize ||
            state.ReceivedBytes + chunk.ChunkData.Length > state.ExpectedSize)
        {
            if (state is not null && incomingFiles.TryRemove(key, out _))
            {
                var exception = new InvalidDataException("O bloco recebido está fora de ordem ou excede o tamanho anunciado.");
                ReportFileTransfer(
                    state.TransferId,
                    state.OriginDeviceId,
                    Path.GetFileName(state.FinalPath),
                    FileTransferDirection.Receiving,
                    FileTransferStage.Failed,
                    state.ReceivedBytes,
                    state.ExpectedSize,
                    state.StartedAt,
                    exception);
                await state.DisposeAsync();
                File.Delete(state.TemporaryPath);
            }

            throw new InvalidDataException("O bloco de arquivo está fora de ordem ou excede o tamanho anunciado.");
        }

        await state.Stream.WriteAsync(chunk.ChunkData.Memory, cancellationToken);
        state.Hash.AppendData(chunk.ChunkData.Span);
        state.NextChunkIndex++;
        state.ReceivedBytes += chunk.ChunkData.Length;
        ReportFileTransfer(
            state.TransferId,
            state.OriginDeviceId,
            Path.GetFileName(state.FinalPath),
            FileTransferDirection.Receiving,
            FileTransferStage.Transferring,
            state.ReceivedBytes,
            state.ExpectedSize,
            state.StartedAt);
    }

    private async Task CompleteIncomingFileAsync(
        string originDeviceId,
        FileTransferEvent completed,
        CancellationToken cancellationToken)
    {
        if (!incomingFiles.TryRemove(OfferKey(completed.TransferId, originDeviceId), out var state))
        {
            throw new InvalidDataException("A conclusão não corresponde a uma transferência ativa.");
        }

        try
        {
            await state.Stream.FlushAsync(cancellationToken);
            await state.Stream.DisposeAsync();
            var actualHash = state.Hash.GetHashAndReset();
            if (state.ReceivedBytes != state.ExpectedSize ||
                !CryptographicOperations.FixedTimeEquals(actualHash, state.ExpectedHash) ||
                (completed.Sha256.Length > 0 &&
                    !CryptographicOperations.FixedTimeEquals(actualHash, completed.Sha256.Span)))
            {
                File.Delete(state.TemporaryPath);
                throw new InvalidDataException("O arquivo recebido falhou na verificação SHA-256.");
            }

            File.Move(state.TemporaryPath, state.FinalPath);
            StatusChanged?.Invoke(
                this,
                new FeatureStatusEventArgs($"Arquivo recebido: {Path.GetFileName(state.FinalPath)}"));
            ReportFileTransfer(
                state.TransferId,
                state.OriginDeviceId,
                Path.GetFileName(state.FinalPath),
                FileTransferDirection.Receiving,
                FileTransferStage.Completed,
                state.ExpectedSize,
                state.ExpectedSize,
                state.StartedAt);
        }
        catch (Exception exception)
        {
            ReportFileTransfer(
                state.TransferId,
                state.OriginDeviceId,
                Path.GetFileName(state.FinalPath),
                FileTransferDirection.Receiving,
                exception is OperationCanceledException ? FileTransferStage.Cancelled : FileTransferStage.Failed,
                state.ReceivedBytes,
                state.ExpectedSize,
                state.StartedAt,
                exception);
            File.Delete(state.TemporaryPath);
            throw;
        }
        finally
        {
            state.Hash.Dispose();
            CryptographicOperations.ZeroMemory(state.ExpectedHash);
        }
    }

    private async Task CancelIncomingFileAsync(string originDeviceId, string transferId)
    {
        if (incomingFiles.TryRemove(OfferKey(transferId, originDeviceId), out var state))
        {
            ReportFileTransfer(
                state.TransferId,
                state.OriginDeviceId,
                Path.GetFileName(state.FinalPath),
                FileTransferDirection.Receiving,
                FileTransferStage.Cancelled,
                state.ReceivedBytes,
                state.ExpectedSize,
                state.StartedAt);
            await state.DisposeAsync();
            File.Delete(state.TemporaryPath);
        }
    }

    private async Task HandleClipboardAsync(
        TrustedDevice origin,
        ClipboardSyncEvent clipboard,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clipboard.Text) || clipboard.Text.Length > 64 * 1024)
        {
            throw new InvalidDataException("O clipboard recebido excede o limite permitido.");
        }

        if (await AuthorizeAsync(
                origin,
                VeyroFeature.Clipboard,
                "substituir o clipboard deste computador",
                cancellationToken))
        {
            ClipboardReceived?.Invoke(this, new ClipboardReceivedEventArgs(origin.DisplayName, clipboard.Text));
        }
    }

    private async Task HandleLinkAsync(
        TrustedDevice origin,
        UrlShareEvent link,
        CancellationToken cancellationToken)
    {
        var uri = ValidateWebUri(link.HyperlinkTarget);
        if (await AuthorizeAsync(origin, VeyroFeature.Links, $"abrir {uri.Host}", cancellationToken))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            StatusChanged?.Invoke(this, new FeatureStatusEventArgs($"Link aberto de {origin.DisplayName}"));
        }
    }

    private async Task HandlePingAsync(
        TrustedDevice origin,
        PingEvent ping,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(ping.RequestId, "D", out _))
        {
            throw new InvalidDataException("O ping não possui um ID válido.");
        }

        if (ping.Action == PingAction.PingRequest)
        {
            await SendAsync(
                new VeyroMessage
                {
                    PingEvent = new PingEvent
                    {
                        RequestId = ping.RequestId,
                        Action = PingAction.PingResponse
                    }
                },
                [origin.DeviceId],
                cancellationToken);
        }
        else if (ping.Action == PingAction.PingResponse &&
            pendingPings.TryRemove(ping.RequestId, out var startedAt))
        {
            RemoteDeviceStateChanged?.Invoke(
                this,
                new RemoteDeviceStateEventArgs(
                    origin.DeviceId,
                    null,
                    null,
                    Stopwatch.GetElapsedTime(startedAt)));
        }
    }

    private async Task HandleNotificationAsync(
        TrustedDevice origin,
        NotificationSyncEvent notification,
        CancellationToken cancellationToken)
    {
        if (notification.SyncAction != NotificationSyncAction.PostNew ||
            !await AuthorizeAsync(
                origin,
                VeyroFeature.Notifications,
                $"mostrar uma notificação de {Limit(notification.AppName, 80)}",
                cancellationToken))
        {
            return;
        }

        NotificationReceived?.Invoke(
            this,
            new VeyroNotificationEventArgs(
                Limit(notification.AppName, 80),
                Limit(notification.Title, 160),
                Limit(notification.TextBody, 500)));
    }

    private async Task HandleMediaControlAsync(
        TrustedDevice origin,
        MediaControlEvent media,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(
                origin,
                VeyroFeature.MediaControl,
                $"controlar a mídia ({media.EventCategory})",
                cancellationToken))
        {
            return;
        }

        if (media.EventCategory is MediaEventCategory.CmdVolUp or MediaEventCategory.CmdVolDown)
        {
            SendMediaKey(media.EventCategory == MediaEventCategory.CmdVolUp ? VolumeUpKey : VolumeDownKey);
            return;
        }

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession()
            ?? throw new InvalidOperationException("Nenhuma sessão de mídia está ativa no Windows.");
        _ = media.EventCategory switch
        {
            MediaEventCategory.CmdPlay => await session.TryPlayAsync(),
            MediaEventCategory.CmdPause => await session.TryPauseAsync(),
            MediaEventCategory.CmdNext => await session.TrySkipNextAsync(),
            MediaEventCategory.CmdPrev => await session.TrySkipPreviousAsync(),
            _ => throw new InvalidDataException("O comando de mídia não é suportado.")
        };
    }

    private async Task HandleSecureCommandAsync(
        TrustedDevice origin,
        CustomCommandEvent command,
        CancellationToken cancellationToken)
    {
        if (command.ExecutionTypeCategory == ExecutionTypeCategory.ExecutionResult)
        {
            StatusChanged?.Invoke(
                this,
                new FeatureStatusEventArgs(
                    command.ExecutionSucceeded ? "Comando remoto concluído" : "Comando remoto recusado"));
            return;
        }

        var accepted = await AuthorizeAsync(
            origin,
            VeyroFeature.SecureCommands,
            $"executar a ação segura {Limit(command.EncodedCommandString, 80)}",
            cancellationToken);
        var succeeded = false;
        var result = "not_authorized";
        if (accepted)
        {
            (succeeded, result) = ExecuteSafeCommand(command);
        }

        await SendAsync(
            new VeyroMessage
            {
                CustomCommandEvent = new CustomCommandEvent
                {
                    CommandTrackingId = command.CommandTrackingId,
                    ExecutionTypeCategory = ExecutionTypeCategory.ExecutionResult,
                    ExecutionSucceeded = succeeded,
                    ExecutionOutput = result
                }
            },
            [origin.DeviceId],
            cancellationToken);
    }

    private async Task HandlePresentationAsync(
        TrustedDevice origin,
        PresentationEvent presentation,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(
                origin,
                VeyroFeature.Presentation,
                $"controlar a apresentação ({presentation.Action})",
                cancellationToken))
        {
            return;
        }

        var keys = presentation.Action switch
        {
            PresentationAction.PresentationStart => "{F5}",
            PresentationAction.PresentationStop => "{ESC}",
            PresentationAction.PresentationBlackoutOn => "b",
            PresentationAction.PresentationBlackoutOff => "b",
            PresentationAction.PresentationTimerSync => null,
            _ => throw new InvalidDataException("A ação de apresentação não é suportada.")
        };
        if (keys is not null)
        {
            System.Windows.Forms.SendKeys.SendWait(keys);
        }
    }

    private async Task HandleRemoteInputAsync(
        TrustedDevice origin,
        RemoteInputEvent input,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(
                origin,
                VeyroFeature.RemoteInput,
                "controlar mouse, teclado ou caneta deste computador",
                cancellationToken))
        {
            return;
        }

        if (!TryConsumeInputRate(origin.DeviceId))
        {
            throw new InvalidDataException("A taxa de entrada remota excedeu 240 eventos por segundo.");
        }

        inputController.Apply(input);
        if (input.InputCommand == RemoteInputCommand.StylusEvent)
        {
            RemoteStylusReceived?.Invoke(this, new RemoteStylusEventArgs(origin.DeviceId, input));
        }
    }

    private async Task HandleRemoteFileAsync(
        TrustedDevice origin,
        RemoteFileEvent remoteFile,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(remoteFile.RequestId, "D", out _))
        {
            throw new InvalidDataException("A solicitação de pasta não possui um ID válido.");
        }

        switch (remoteFile.Action)
        {
            case RemoteFileAction.ListRequest:
                if (!await AuthorizeAsync(
                        origin,
                        VeyroFeature.SharedFolders,
                        "consultar as pastas compartilhadas",
                        cancellationToken))
                {
                    await SendRemoteFileRejectionAsync(origin.DeviceId, remoteFile.RequestId, cancellationToken);
                    return;
                }

                var entries = sharedFolderStore.List(remoteFile.ParentDocumentId);
                var response = new RemoteFileEvent
                {
                    RequestId = remoteFile.RequestId,
                    Action = RemoteFileAction.ListResponse,
                    ParentDocumentId = remoteFile.ParentDocumentId
                };
                response.Entries.Add(entries.Select(entry => new RemoteFileEntry
                {
                    DocumentId = entry.DocumentId,
                    DisplayName = entry.DisplayName,
                    MimeType = entry.MimeType,
                    SizeBytes = entry.SizeBytes,
                    IsDirectory = entry.IsDirectory
                }));
                await SendAsync(
                    new VeyroMessage { RemoteFileEvent = response },
                    [origin.DeviceId],
                    cancellationToken);
                break;
            case RemoteFileAction.ListResponse:
                RemoteFilesReceived?.Invoke(
                    this,
                    new RemoteFilesEventArgs(
                        origin.DeviceId,
                        remoteFile.ParentDocumentId,
                        remoteFile.Entries.ToArray()));
                break;
            case RemoteFileAction.DownloadRequest:
                if (!await AuthorizeAsync(
                        origin,
                        VeyroFeature.SharedFolders,
                        "baixar um arquivo de uma pasta compartilhada",
                        cancellationToken))
                {
                    await SendRemoteFileRejectionAsync(origin.DeviceId, remoteFile.RequestId, cancellationToken);
                    return;
                }

                var filePath = sharedFolderStore.ResolveFile(remoteFile.RequestedDocumentId);
                await SendFilesAsync([filePath], [origin.DeviceId], cancellationToken);
                break;
            case RemoteFileAction.DownloadRejected:
                StatusChanged?.Invoke(
                    this,
                    new FeatureStatusEventArgs($"Acesso à pasta recusado por {origin.DisplayName}"));
                break;
            default:
                throw new InvalidDataException("A ação de pasta compartilhada não é suportada.");
        }
    }

    private Task SendRemoteFileRejectionAsync(
        string destinationDeviceId,
        string requestId,
        CancellationToken cancellationToken) =>
        SendAsync(
            new VeyroMessage
            {
                RemoteFileEvent = new RemoteFileEvent
                {
                    RequestId = requestId,
                    Action = RemoteFileAction.DownloadRejected,
                    ResultMessage = "not_authorized"
                }
            },
            [destinationDeviceId],
            cancellationToken);

    private async Task<bool> AuthorizeAsync(
        TrustedDevice origin,
        VeyroFeature feature,
        string description,
        CancellationToken cancellationToken)
    {
        var policy = permissionStore.GetPolicy(origin.DeviceId, feature);
        if (policy != FeatureAccessPolicy.Ask)
        {
            return policy == FeatureAccessPolicy.Allow;
        }

        var request = new FeatureAuthorizationEventArgs(
            origin.DeviceId,
            origin.DisplayName,
            feature,
            description);
        AuthorizationRequested?.Invoke(this, request);
        try
        {
            return await request.WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private Task SendFileEventAsync(
        FileTransferEvent fileEvent,
        string destinationDeviceId,
        CancellationToken cancellationToken) =>
        SendAsync(
            new VeyroMessage { FileTransferEvent = fileEvent },
            [destinationDeviceId],
            cancellationToken);

    private Task SendAsync(
        VeyroMessage message,
        IReadOnlyCollection<string> destinationDeviceIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        message.ProtocolVersion = ProtocolContract.AndroidFeatureProtocolVersion;
        return channelCoordinator.SendApplicationMessageAsync(message, destinationDeviceIds, cancellationToken);
    }

    private static (bool Succeeded, string Result) ExecuteSafeCommand(CustomCommandEvent command)
    {
        if (command.ExecutionTypeCategory != ExecutionTypeCategory.SystemUriActionCall)
        {
            return (false, "unsupported_command_type");
        }

        var target = command.EncodedCommandString.Trim();
        if (string.Equals(target, "lock-workstation", StringComparison.OrdinalIgnoreCase))
        {
            return (LockWorkStation(), "lock_requested");
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "ms-settings", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "uri_not_allowed");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return (true, "action_started");
    }

    private static bool IsAllowedOutgoingCommand(string command)
    {
        if (string.Equals(command, "lock-workstation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(command, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "ms-settings", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ValidateWebUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Somente links HTTP e HTTPS são aceitos.", nameof(value));
        }

        return uri;
    }

    private static string CreateUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
        }

        return candidate;
    }

    private static string OfferKey(string transferId, string deviceId) => $"{transferId}:{deviceId}";

    private void ReportFileTransfer(
        string transferId,
        string deviceId,
        string fileName,
        FileTransferDirection direction,
        FileTransferStage stage,
        long transferredBytes,
        long totalBytes,
        long startedAt,
        Exception? error = null) =>
        FileTransferProgressChanged?.Invoke(
            this,
            new FileTransferProgressEventArgs(
                transferId,
                deviceId,
                trustStore.FindActive(deviceId)?.DisplayName ?? deviceId[..Math.Min(deviceId.Length, 8)],
                fileName,
                direction,
                stage,
                transferredBytes,
                totalBytes,
                Stopwatch.GetElapsedTime(startedAt),
                error));

    private static string InferMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static string FormatSize(long size) => size < 1024 * 1024
        ? $"{size / 1024d:F1} KiB"
        : $"{size / (1024d * 1024):F1} MiB";

    private static string Limit(string value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? "Veyro"
            : value[..Math.Min(value.Length, maximumLength)];

    private bool TryConsumeInputRate(string deviceId)
    {
        var window = inputRateWindows.GetOrAdd(deviceId, _ => new InputRateWindow());
        lock (window)
        {
            var now = Stopwatch.GetTimestamp();
            if (window.StartedAt == 0 || Stopwatch.GetElapsedTime(window.StartedAt, now) >= TimeSpan.FromSeconds(1))
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            return ++window.Count <= 240;
        }
    }

    private static void SendMediaKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        channelCoordinator.EnvelopeReceived -= ChannelCoordinator_EnvelopeReceived;
        await lifetime.CancelAsync();
        foreach (var state in incomingFiles.Values)
        {
            await state.DisposeAsync();
            File.Delete(state.TemporaryPath);
        }

        incomingFiles.Clear();
        lifetime.Dispose();
    }

    private sealed class IncomingFileState(
        string originDeviceId,
        string transferId,
        string finalPath,
        string temporaryPath,
        long expectedSize,
        byte[] expectedHash,
        FileStream stream,
        IncrementalHash hash,
        long startedAt) : IAsyncDisposable
    {
        public string OriginDeviceId { get; } = originDeviceId;
        public string TransferId { get; } = transferId;
        public string FinalPath { get; } = finalPath;
        public string TemporaryPath { get; } = temporaryPath;
        public long ExpectedSize { get; } = expectedSize;
        public byte[] ExpectedHash { get; } = expectedHash;
        public FileStream Stream { get; } = stream;
        public IncrementalHash Hash { get; } = hash;
        public long StartedAt { get; } = startedAt;
        public uint NextChunkIndex { get; set; }
        public long ReceivedBytes { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            Hash.Dispose();
            CryptographicOperations.ZeroMemory(ExpectedHash);
        }
    }

    private sealed class InputRateWindow
    {
        public long StartedAt { get; set; }

        public int Count { get; set; }
    }
}
