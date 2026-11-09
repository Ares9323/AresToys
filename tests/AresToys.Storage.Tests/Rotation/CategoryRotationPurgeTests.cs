using System.Text;
using Microsoft.Extensions.Options;
using AresToys.Core.Domain;
using AresToys.Storage.Blobs;
using AresToys.Storage.Items;
using AresToys.Storage.Options;
using AresToys.Storage.Protection;
using AresToys.Storage.Rotation;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Rotation;

public class CategoryRotationPurgeTests
{
    private static (IItemStore Items, IBlobStore Blobs, CategoryRotationService Rotation) Build(TempDatabaseFixture fx)
    {
        var protector = new DpapiPayloadProtector();
        var items = new ItemStore(fx.Database, new ItemSerializer(protector));
        var blobs = new FileSystemBlobStore(fx.Paths);
        var rotation = new CategoryRotationService(fx.Database, blobs, Microsoft.Extensions.Options.Options.Create(fx.Options));
        return (items, blobs, rotation);
    }

    private static NewItem TextItem(string text, DateTimeOffset created, bool pinned = false)
        => new(
            Kind: ItemKind.Text,
            Source: ItemSource.Clipboard,
            CreatedAt: created,
            Payload: Encoding.UTF8.GetBytes(text),
            PayloadSize: Encoding.UTF8.GetByteCount(text),
            Pinned: pinned,
            SearchText: text);

    private static async Task SetDeletedAtAsync(TempDatabaseFixture fx, long id, DateTimeOffset when)
    {
        await using var lease = await fx.Database.AcquireAsync(CancellationToken.None);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "UPDATE items SET deleted_at = $at WHERE id = $id;";
        cmd.Parameters.AddWithValue("$at", when.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PurgeAsync_HardDeletesPastGracePeriod()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, _, rotation) = Build(fx);

        var id = await items.AddAsync(TextItem("victim", DateTimeOffset.UtcNow.AddDays(-100)), CancellationToken.None);
        await items.SoftDeleteAsync(id, CancellationToken.None);
        await SetDeletedAtAsync(fx, id, DateTimeOffset.UtcNow.AddHours(-48));

        var deleted = await rotation.PurgeAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Null(await items.GetByIdAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeAsync_KeepsRowsInsideGracePeriod()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, _, rotation) = Build(fx);

        var id = await items.AddAsync(TextItem("recent", DateTimeOffset.UtcNow), CancellationToken.None);
        await items.SoftDeleteAsync(id, CancellationToken.None);
        await SetDeletedAtAsync(fx, id, DateTimeOffset.UtcNow.AddHours(-1));

        var deleted = await rotation.PurgeAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.NotNull(await items.GetByIdAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeAsync_DeletesPinnedDeletedRows_KeepsLiveOnes()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, _, rotation) = Build(fx);

        var pinnedDeleted = await items.AddAsync(TextItem("pinned deleted", DateTimeOffset.UtcNow, pinned: true), CancellationToken.None);
        await items.SoftDeleteAsync(pinnedDeleted, CancellationToken.None);
        await SetDeletedAtAsync(fx, pinnedDeleted, DateTimeOffset.UtcNow.AddDays(-30));
        var pinnedLive = await items.AddAsync(TextItem("pinned live", DateTimeOffset.UtcNow.AddDays(-100), pinned: true), CancellationToken.None);
        var live = await items.AddAsync(TextItem("live", DateTimeOffset.UtcNow.AddDays(-100)), CancellationToken.None);

        var deleted = await rotation.PurgeAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Null(await items.GetByIdAsync(pinnedDeleted, CancellationToken.None));
        Assert.NotNull(await items.GetByIdAsync(pinnedLive, CancellationToken.None));
        Assert.NotNull(await items.GetByIdAsync(live, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeAsync_RemovesOrphanBlobsButKeepsReferencedOnes()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, blobs, rotation) = Build(fx);

        await blobs.AddAsync(new byte[] { 1, 2 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        await blobs.AddAsync(new byte[] { 3, 4 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        var keptRef = await blobs.AddAsync(new byte[] { 5, 6 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        await items.AddAsync(
            new NewItem(
                Kind: ItemKind.Image,
                Source: ItemSource.CaptureRegion,
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: ReadOnlyMemory<byte>.Empty,
                PayloadSize: 2,
                BlobRef: keptRef,
                SearchText: "kept blob"),
            CancellationToken.None);

        await rotation.PurgeAsync(CancellationToken.None);

        var remaining = new List<string>();
        await foreach (var r in blobs.EnumerateAllAsync(CancellationToken.None)) remaining.Add(r);
        Assert.Equal(new[] { keptRef }, remaining);
    }

    [Fact]
    public async Task PurgeAsync_RemovesBlobOfHardDeletedRow()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, blobs, rotation) = Build(fx);

        var blobRef = await blobs.AddAsync(new byte[] { 9, 9 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        var id = await items.AddAsync(
            new NewItem(
                Kind: ItemKind.Image,
                Source: ItemSource.CaptureRegion,
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: ReadOnlyMemory<byte>.Empty,
                PayloadSize: 2,
                BlobRef: blobRef,
                SearchText: "doomed blob"),
            CancellationToken.None);
        await items.SoftDeleteAsync(id, CancellationToken.None);
        await SetDeletedAtAsync(fx, id, DateTimeOffset.UtcNow.AddHours(-48));

        var deleted = await rotation.PurgeAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        await foreach (var _ in blobs.EnumerateAllAsync(CancellationToken.None))
            Assert.Fail("the blob of the purged row should be gone");
    }

    [Fact]
    public async Task PurgeAsync_ZeroGrace_PurgesEverySoftDeletedNonPinnedRow()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var items = new ItemStore(fx.Database, new ItemSerializer(new DpapiPayloadProtector()));
        var options = new StorageOptions();
        options.Rotation.SoftDeleteGracePeriod = TimeSpan.Zero;
        var rotation = new CategoryRotationService(fx.Database, new FileSystemBlobStore(fx.Paths), Microsoft.Extensions.Options.Options.Create(options));

        var id = await items.AddAsync(TextItem("gone", DateTimeOffset.UtcNow), CancellationToken.None);
        await items.SoftDeleteAsync(id, CancellationToken.None);
        await Task.Delay(20);

        Assert.Equal(1, await rotation.PurgeAsync(CancellationToken.None));
        Assert.Null(await items.GetByIdAsync(id, CancellationToken.None));
    }
}
