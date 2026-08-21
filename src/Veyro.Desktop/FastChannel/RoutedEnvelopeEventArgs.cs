using Veyro.Protocol;

namespace Veyro.Desktop.FastChannel;

public sealed class RoutedEnvelopeEventArgs(TransportEnvelope envelope, VeyroMessage message) : EventArgs
{
    public TransportEnvelope Envelope { get; } = envelope;

    public VeyroMessage Message { get; } = message;
}
