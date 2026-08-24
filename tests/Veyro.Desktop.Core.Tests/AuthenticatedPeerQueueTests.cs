using Veyro.Desktop.Core.Transport;

namespace Veyro.Desktop.Core.Tests;

public sealed class AuthenticatedPeerQueueTests
{
    [Fact]
    public void Sequential_wifi_links_claim_their_distinct_ble_identities()
    {
        var queue = new AuthenticatedPeerQueue();

        Assert.True(queue.Enqueue("android-a"));
        Assert.True(queue.Enqueue("android-b"));
        Assert.False(queue.Enqueue("android-a"));
        Assert.Equal("android-a", queue.Claim(_ => true));
        Assert.Equal("android-b", queue.Claim(_ => true));
        Assert.Null(queue.Claim(_ => true));
    }

    [Fact]
    public void Ineligible_or_already_connected_identity_is_skipped()
    {
        var queue = new AuthenticatedPeerQueue();
        queue.Enqueue("already-connected");
        queue.Enqueue("new-android");

        Assert.Equal("new-android", queue.Claim(id => id != "already-connected"));
    }
}
