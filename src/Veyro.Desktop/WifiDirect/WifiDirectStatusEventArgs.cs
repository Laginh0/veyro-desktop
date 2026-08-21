namespace Veyro.Desktop.WifiDirect;

public sealed class WifiDirectStatusEventArgs(string message, Exception? error = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Error { get; } = error;
}
