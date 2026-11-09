using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Pipeline.Tests.Fixtures;
using Xunit;

namespace AresToys.Pipeline.Tests.Profiles;

public class SqlitePipelineProfileStoreTests
{
    private static IPipelineProfileStore Create(TempPipelineDatabaseFixture fx)
        => new SqlitePipelineProfileStore(fx.Database);

    private static PipelineProfile Sample(string id) => new(
        Id: id,
        DisplayName: id.ToUpperInvariant(),
        Trigger: "event:test",
        Steps: new[]
        {
            new PipelineStep("arestoys.add-to-history"),
            new PipelineStep("arestoys.save-to-file", Enabled: false)
        });

    [Fact]
    public async Task Upsert_Then_Get_RoundTripsProfile()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var store = Create(fx);
        var profile = Sample("p1");

        await store.UpsertAsync(profile, CancellationToken.None);
        var loaded = await store.GetAsync("p1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("p1", loaded!.Id);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.False(loaded.Steps[1].Enabled);
    }

    [Fact]
    public async Task Upsert_OverwritesExistingProfile()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var store = Create(fx);

        await store.UpsertAsync(Sample("p1"), CancellationToken.None);
        await store.UpsertAsync(Sample("p1") with { DisplayName = "renamed" }, CancellationToken.None);

        var loaded = await store.GetAsync("p1", CancellationToken.None);
        Assert.Equal("renamed", loaded!.DisplayName);
    }

    [Fact]
    public async Task List_ReturnsAllProfilesSortedById()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var store = Create(fx);
        await store.UpsertAsync(Sample("b"), CancellationToken.None);
        await store.UpsertAsync(Sample("a"), CancellationToken.None);
        await store.UpsertAsync(Sample("c"), CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, list.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrueAndRemoves()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var store = Create(fx);
        await store.UpsertAsync(Sample("p1"), CancellationToken.None);

        Assert.True(await store.DeleteAsync("p1", CancellationToken.None));
        Assert.Null(await store.GetAsync("p1", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Missing_ReturnsFalse()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var store = Create(fx);

        Assert.False(await store.DeleteAsync("never-there", CancellationToken.None));
    }
}
