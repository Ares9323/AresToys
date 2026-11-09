using AresToys.Storage.Protection;
using AresToys.Storage.Settings;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Settings;

public class SqliteSettingsStoreTests
{
    private static ISettingsStore CreateStore(TempDatabaseFixture fx)
        => new SqliteSettingsStore(fx.Database, new DpapiPayloadProtector());

    [Fact]
    public async Task Set_Then_Get_RoundTripsPlainValue()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.SetAsync("ui.theme", "dark", sensitive: false, CancellationToken.None);

        Assert.Equal("dark", await store.GetAsync("ui.theme", CancellationToken.None));
    }

    [Fact]
    public async Task Set_Then_Get_RoundTripsSensitiveValueViaDpapi()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.SetAsync("uploader.imgur.client_id", "secret-id", sensitive: true, CancellationToken.None);

        Assert.Equal("secret-id", await store.GetAsync("uploader.imgur.client_id", CancellationToken.None));
    }

    [Fact]
    public async Task Set_OverwritesExisting()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.SetAsync("k", "v1", sensitive: false, CancellationToken.None);
        await store.SetAsync("k", "v2", sensitive: false, CancellationToken.None);

        Assert.Equal("v2", await store.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Get_OfMissingKey_ReturnsNull()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        Assert.Null(await store.GetAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Remove_OfExisting_ReturnsTrueAndDeletes()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        await store.SetAsync("k", "v", sensitive: false, CancellationToken.None);
        Assert.True(await store.RemoveAsync("k", CancellationToken.None));
        Assert.Null(await store.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Remove_OfMissing_ReturnsFalse()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var store = CreateStore(fx);

        Assert.False(await store.RemoveAsync("never-there", CancellationToken.None));
    }
}
