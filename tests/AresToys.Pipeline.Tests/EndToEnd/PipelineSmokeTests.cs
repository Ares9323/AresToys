using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Pipeline;
using AresToys.Pipeline.DependencyInjection;
using AresToys.Pipeline.Profiles;
using AresToys.Storage.Database;
using AresToys.Storage.DependencyInjection;
using AresToys.Storage.Items;
using Xunit;

namespace AresToys.Pipeline.Tests.EndToEnd;

public class PipelineSmokeTests
{
    [Fact]
    public async Task EndToEnd_OnClipboardProfile_StoresIncomingItem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AresToys.PipelineSmoke", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAresToysStorage(opts => opts.RootDirectoryOverride = tempRoot);
            services.AddAresToysPipeline();
            await using var sp = services.BuildServiceProvider();

            var db = sp.GetRequiredService<IAresToysDatabase>();
            await db.InitializeAsync(CancellationToken.None);

            var seeder = sp.GetRequiredService<PipelineProfileSeeder>();
            await seeder.SeedAsync(CancellationToken.None);

            var profileStore = sp.GetRequiredService<IPipelineProfileStore>();
            var profile = await profileStore.GetAsync(DefaultPipelineProfiles.OnClipboardId, CancellationToken.None);
            Assert.NotNull(profile);

            var executor = sp.GetRequiredService<PipelineExecutor>();
            var ctx = new PipelineContext(sp);
            var newItem = new NewItem(
                Kind: ItemKind.Text,
                Source: ItemSource.Clipboard,
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: Encoding.UTF8.GetBytes("smoke text"),
                PayloadSize: 10,
                SearchText: "smoke text");
            ctx.Bag[PipelineBagKeys.NewItem] = newItem;

            await executor.RunAsync(profile!, ctx, CancellationToken.None);

            var id = Assert.IsType<long>(ctx.Bag[PipelineBagKeys.ItemId]);
            var items = sp.GetRequiredService<IItemStore>();
            var stored = await items.GetByIdAsync(id, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("smoke text", Encoding.UTF8.GetString(stored!.Payload.Span));
        }
        finally
        {
            // Pooled connections keep the database files open until their pool is cleared.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
