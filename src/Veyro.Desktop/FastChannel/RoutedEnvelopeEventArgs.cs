using Veyro.Protocol;

namespace Veyro.Desktop.FastChannel;

public sealed class RoutedEnvelopeEventArgs(TransportEnvelope envelope) : EventArgs
{
    public TransportEnvelope Envelope { get; } = envelope;
}
