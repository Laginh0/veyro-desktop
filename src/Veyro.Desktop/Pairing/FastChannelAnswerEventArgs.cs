namespace Veyro.Desktop.Pairing;

public sealed class FastChannelAnswerEventArgs(Veyro.Protocol.FastChannelAnswer answer) : EventArgs
{
    public Veyro.Protocol.FastChannelAnswer Answer { get; } = answer;
}
