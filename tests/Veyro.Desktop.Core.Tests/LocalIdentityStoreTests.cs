using System.Text;
using Veyro.Desktop.Core.Identity;

namespace Veyro.Desktop.Core.Tests;

public sealed class LocalIdentityStoreTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"veyro-identity-test-{Guid.NewGuid():N}");

    [Fact]
    public void Identity_is_stable_and_not_stored_as_plaintext()
    {
        var identityFile = Path.Combine(temporaryDirectory, "identity.dat");
        var store = new LocalIdentityStore(identityFile, new DpapiIdentityProtector());

        var created = store.LoadOrCreate();
        var loaded = store.LoadOrCreate();
        var storedBytes = File.ReadAllBytes(identityFile);

        Assert.Equal(created, loaded);
        Assert.Matches("^[a-f0-9]{16}$", created.DeviceId);
        Assert.DoesNotContain(created.DeviceId, Encoding.UTF8.GetString(storedBytes));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
