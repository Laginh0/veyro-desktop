using Veyro.Desktop.Core.Features;
using Veyro.Protocol;

namespace Veyro.Desktop.Features;

public sealed class FeatureStatusEventArgs(string message, Exception? error = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Error { get; } = error;
}

public sealed class FeatureAuthorizationEventArgs(
    string deviceId,
    string deviceName,
    VeyroFeature feature,
    string description) : EventArgs
{
    private readonly TaskCompletionSource<bool> decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DeviceId { get; } = deviceId;

    public string DeviceName { get; } = deviceName;

    public VeyroFeature Feature { get; } = feature;

    public string Description { get; } = description;

    public void Complete(bool accepted) => decision.TrySetResult(accepted);

    internal Task<bool> WaitAsync(CancellationToken cancellationToken) =>
        decision.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
}

public sealed class ClipboardReceivedEventArgs(string deviceName, string text) : EventArgs
{
    public string DeviceName { get; } = deviceName;

    public string Text { get; } = text;
}

public sealed class VeyroNotificationEventArgs(string appName, string title, string body) : EventArgs
{
    public string AppName { get; } = appName;

    public string Title { get; } = title;

    public string Body { get; } = body;
}

public sealed class RemoteDeviceStateEventArgs(
    string deviceId,
    BatteryStatus? battery,
    ConnectivityStatus? connectivity,
    TimeSpan? pingRoundTrip) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    public BatteryStatus? Battery { get; } = battery;

    public ConnectivityStatus? Connectivity { get; } = connectivity;

    public TimeSpan? PingRoundTrip { get; } = pingRoundTrip;
}

public sealed class RemoteStylusEventArgs(string deviceId, RemoteInputEvent input) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    public RemoteInputEvent Input { get; } = input;
}

public sealed class RemoteFilesEventArgs(
    string deviceId,
    string parentDocumentId,
    IReadOnlyList<RemoteFileEntry> entries) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    public string ParentDocumentId { get; } = parentDocumentId;

    public IReadOnlyList<RemoteFileEntry> Entries { get; } = entries;
}
