namespace Veyro.Desktop.FastChannel;

public sealed class FastChannelStatusEventArgs(string message, Exception? error = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Error { get; } = error;
}
