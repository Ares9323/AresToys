using System.Text;
using Microsoft.Extensions.DependencyInjection;
using AresToys.Core.Domain;
using AresToys.Storage.Blobs;
using AresToys.Storage.Database;
using AresToys.Storage.DependencyInjection;
using AresToys.Storage.Items;
using AresToys.Storage.Rotation;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.Storage.Tests.EndToEnd;

public class StorageSmokeTests
{
    [Fact]
    public async Task EndToEnd_DiWiring_CanInitializeAndExerciseAllStores()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AresToys.Smoke", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAresToysStorage(opts => opts.RootDirectoryOverride = tempRoot);
            await using var sp = services.BuildServiceProvider();

            var db = sp.GetRequiredService<IAresToysDatabase>();
            await db.InitializeAsync(CancellationToken.None);

            var items = sp.GetRequiredService<IItemStore>();
            var blobs = sp.GetRequiredService<IBlobStore>();
            var settings = sp.GetRequiredService<ISettingsStore>();
            var rotation = sp.GetRequiredService<CategoryRotationService>();

            // 1. Insert an item with a small inline payload
            var smallId = await items.AddAsync(
                new NewItem(
                    Kind: ItemKind.Text,
                    Source: ItemSource.Clipboard,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Payload: Encoding.UTF8.GetBytes("inline payload"),
                    PayloadSize: 14,
                    SearchText: "inline payload"),
                CancellationToken.None);

            // 2. Insert an item that points to a blob (caller-side decision based on threshold)
            var largePayload = new byte[200 * 1024];
            new Random(42).NextBytes(largePayload);
            var blobRef = await blobs.AddAsync(largePayload, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
            var largeId = await items.AddAsync(
                new NewItem(
                    Kind: ItemKind.Image,
                    Source: ItemSource.CaptureRegion,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Payload: ReadOnlyMemory<byte>.Empty,
                    PayloadSize: largePayload.LongLength,
                    BlobRef: blobRef,
                    SearchText: "large screenshot"),
                CancellationToken.None);

            // 3. Round-trip both
            Assert.NotNull(await items.GetByIdAsync(smallId, CancellationToken.None));
            var loadedLarge = (await items.GetByIdAsync(largeId, CancellationToken.None))!;
            Assert.Equal(blobRef, loadedLarge.BlobRef);
            var blobBytes = await blobs.ReadAllAsync(loadedLarge.BlobRef!, CancellationToken.None);
            Assert.Equal(largePayload, blobBytes);

            // 4. Search
            var hits = await items.ListAsync(new ItemQuery(Search: "screenshot"), CancellationToken.None);
            Assert.Single(hits);
            Assert.Equal(largeId, hits[0].Id);

            // 5. Settings round-trip with sensitive flag
            await settings.SetAsync("uploader.imgur.client_id", "deadbeef", sensitive: true, CancellationToken.None);
            Assert.Equal("deadbeef", await settings.GetAsync("uploader.imgur.client_id", CancellationToken.None));

            // 6. Rotation: cap to 1, expect at least one item soft-deleted
            await using (var lease = await db.AcquireAsync(CancellationToken.None))
            {
                await using var cap = lease.Connection.CreateCommand();
                cap.CommandText = "UPDATE categories SET max_items = 1 WHERE name = 'Clipboard';";
                await cap.ExecuteNonQueryAsync();
            }
            Assert.True(await rotation.RunAsync(CancellationToken.None) >= 1);
            // Soft-deleted rows are inside the default grace period, so the purge keeps them.
            Assert.Equal(0, await rotation.PurgeAsync(CancellationToken.None));
        }
        finally
        {
            // Pooled connections keep the database files open until their pool is cleared.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
