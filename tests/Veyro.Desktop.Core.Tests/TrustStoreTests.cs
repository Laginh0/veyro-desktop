using System.Text;
using Veyro.Desktop.Core.Discovery;
using Veyro.Desktop.Core.Identity;
using Veyro.Desktop.Core.Trust;

namespace Veyro.Desktop.Core.Tests;

public sealed class TrustStoreTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"veyro-trust-test-{Guid.NewGuid():N}");

    [Fact]
    public void Trust_store_is_protected_persistent_and_revocable()
    {
        var path = Path.Combine(temporaryDirectory, "trust.dat");
        var store = new TrustStore(path, new DpapiIdentityProtector());
        var key = PairingSessionTests.CreateIdentityKey();
        var device = new TrustedDevice(
            "2222222222222222",
            "Celular",
            Convert.ToBase64String(key.PublicKeySpki),
            VeyroCapability.BleControl,
            10,
            10,
            null);

        store.Trust(device);

        Assert.Equal(device.DeviceId, store.FindActive(device.DeviceId)?.DeviceId);
        Assert.DoesNotContain(device.DeviceId, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.True(store.MarkSeen(device.DeviceId, DateTimeOffset.FromUnixTimeMilliseconds(20)));
        Assert.Equal(20, store.FindActive(device.DeviceId)?.LastSeenAtUnixMilliseconds);
        Assert.True(store.Revoke(device.DeviceId, DateTimeOffset.FromUnixTimeMilliseconds(30)));
        Assert.Null(store.FindActive(device.DeviceId));
        Assert.True(Assert.Single(store.Snapshot()).IsRevoked);
    }

    [Fact]
    public void Trusted_peer_must_sign_the_exact_reconnect_challenge()
    {
        var key = PairingSessionTests.CreateIdentityKey();
        var trusted = new TrustedDevice(
            "2222222222222222",
            "Celular",
            Convert.ToBase64String(key.PublicKeySpki),
            VeyroCapability.BleControl,
            10,
            10,
            null);
        var challenge = TrustedPeerAuthenticator.CreateChallenge();
        var signature = TrustedPeerAuthenticator.Sign(key, trusted.DeviceId, challenge);

        Assert.True(TrustedPeerAuthenticator.Verify(trusted, challenge, signature));
        challenge[0] ^= 0xff;
        Assert.False(TrustedPeerAuthenticator.Verify(trusted, challenge, signature));
    }

    [Fact]
    public void Identity_key_is_stable_and_protected_at_rest()
    {
        var path = Path.Combine(temporaryDirectory, "identity-key.dat");
        var store = new LocalIdentityKeyStore(path, new DpapiIdentityProtector());

        var created = store.LoadOrCreate();
        var loaded = store.LoadOrCreate();

        Assert.Equal(created.PublicKeySpki, loaded.PublicKeySpki);
        Assert.Equal(created.PrivateKeyPkcs8, loaded.PrivateKeyPkcs8);
        Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf(created.PrivateKeyPkcs8));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
