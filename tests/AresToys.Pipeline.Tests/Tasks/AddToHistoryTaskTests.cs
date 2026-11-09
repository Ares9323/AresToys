using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Pipeline.Tests.Fixtures;
using AresToys.Storage.Items;
using AresToys.Storage.Protection;
using Xunit;

namespace AresToys.Pipeline.Tests.Tasks;

public class AddToHistoryTaskTests
{
    private static (AddToHistoryTask Task, IItemStore Store) Build(TempPipelineDatabaseFixture fx)
    {
        var protector = new DpapiPayloadProtector();
        var store = new ItemStore(fx.Database, new ItemSerializer(protector));
        return (new AddToHistoryTask(store, NullLogger<AddToHistoryTask>.Instance), store);
    }

    private static NewItem TextItem(string text) => new(
        Kind: ItemKind.Text,
        Source: ItemSource.Clipboard,
        CreatedAt: DateTimeOffset.UtcNow,
        Payload: Encoding.UTF8.GetBytes(text),
        PayloadSize: Encoding.UTF8.GetByteCount(text),
        SearchText: text);

    [Fact]
    public async Task ExecuteAsync_StoresItemAndPopulatesItemIdInBag()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (task, store) = Build(fx);
        var ctx = new PipelineContext(new ServiceCollection().BuildServiceProvider());
        ctx.Bag[PipelineBagKeys.NewItem] = TextItem("hello");

        await task.ExecuteAsync(ctx, config: null, CancellationToken.None);

        Assert.True(ctx.Bag.ContainsKey(PipelineBagKeys.ItemId));
        var id = (long)ctx.Bag[PipelineBagKeys.ItemId];
        Assert.NotNull(await store.GetByIdAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NoNewItemInBag_DoesNothing()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (task, store) = Build(fx);
        var ctx = new PipelineContext(new ServiceCollection().BuildServiceProvider());

        await task.ExecuteAsync(ctx, config: null, CancellationToken.None);

        Assert.False(ctx.Bag.ContainsKey(PipelineBagKeys.ItemId));
        var any = await store.ListAsync(new ItemQuery(), CancellationToken.None);
        Assert.Empty(any);
    }

    [Fact]
    public async Task ExecuteAsync_WrongTypeInBag_Skips()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (task, store) = Build(fx);
        var ctx = new PipelineContext(new ServiceCollection().BuildServiceProvider());
        ctx.Bag[PipelineBagKeys.NewItem] = "not a NewItem";

        await task.ExecuteAsync(ctx, config: null, CancellationToken.None);

        var any = await store.ListAsync(new ItemQuery(), CancellationToken.None);
        Assert.Empty(any);
    }
}
