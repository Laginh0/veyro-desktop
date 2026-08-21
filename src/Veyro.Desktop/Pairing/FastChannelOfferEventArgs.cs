namespace Veyro.Desktop.Pairing;

public sealed class FastChannelOfferEventArgs(Veyro.Protocol.FastChannelOffer offer) : EventArgs
{
    public Veyro.Protocol.FastChannelOffer Offer { get; } = offer;
}
