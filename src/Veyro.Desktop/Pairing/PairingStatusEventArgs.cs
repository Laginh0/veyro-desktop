namespace Veyro.Desktop.Pairing;

public sealed class PairingStatusEventArgs(string message, Exception? error = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Error { get; } = error;
}
