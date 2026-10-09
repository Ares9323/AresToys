using System.Text;
using Microsoft.Data.Sqlite;
using AresToys.Core.Domain;
using AresToys.Storage.Items;
using AresToys.Storage.Protection;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Items;

/// <summary>Clipboard tags (issue #4): tag definitions, item links, the AND filter, search
/// over tag names and cleanup on hard delete.</summary>
public class TagStoreTests
{
    private static (IItemStore Items, ITagStore Tags) CreateStores(TempDatabaseFixture fx)
        => (new ItemStore(fx.Database, new ItemSerializer(new DpapiPayloadProtector())), new SqliteTagStore(fx.Database));

    private static NewItem TextItem(string text, string category = Category.Default, bool pinned = false)
        => new(
            Kind: ItemKind.Text,
            Source: ItemSource.Clipboard,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: Encoding.UTF8.GetBytes(text),
            PayloadSize: Encoding.UTF8.GetByteCount(text),
            Pinned: pinned,
            SearchText: text,
            Category: category);

    private static async Task<long[]> ListIdsAsync(IItemStore items, ItemQuery query)
        => (await items.ListAsync(query, CancellationToken.None)).Select(r => r.Id).OrderBy(id => id).ToArray();

    [Fact]
    public async Task GetOrCreate_NormalisesName_AndIsCaseInsensitive()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (_, tags) = CreateStores(fx);

        var first = await tags.GetOrCreateAsync("  Licenses \t  MIT ", "#ff8800", CancellationToken.None);
        var again = await tags.GetOrCreateAsync("licenses mit", "#000000", CancellationToken.None);

        Assert.Equal("Licenses MIT", first.Name);
        Assert.Equal("#FF8800", first.Color);
        Assert.Equal(first.Id, again.Id);
        // An existing tag keeps its colour.
        Assert.Equal("#FF8800", again.Color);
        Assert.Single(await tags.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreate_EmptyName_Throws_AndInvalidColourBecomesNeutral()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (_, tags) = CreateStores(fx);

        await Assert.ThrowsAsync<ArgumentException>(() => tags.GetOrCreateAsync("   ", null, CancellationToken.None));
        var tag = await tags.GetOrCreateAsync("regex", "not-a-colour", CancellationToken.None);
        Assert.Null(tag.Color);
    }

    [Fact]
    public async Task AddAndRemoveTag_RoundTripOnItemRecord()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("hello"), CancellationToken.None);
        var t1 = await tags.GetOrCreateAsync("one", null, CancellationToken.None);
        var t2 = await tags.GetOrCreateAsync("two", null, CancellationToken.None);

        Assert.True(await items.AddTagAsync(id, t2.Id, CancellationToken.None));
        Assert.True(await items.AddTagAsync(id, t1.Id, CancellationToken.None));
        // Idempotent: the second add of the same link is a no-op.
        Assert.False(await items.AddTagAsync(id, t1.Id, CancellationToken.None));

        var loaded = await items.GetByIdAsync(id, CancellationToken.None);
        Assert.Equal(new[] { t1.Id, t2.Id }, loaded!.Tags.ToArray());
        var listed = (await items.ListAsync(new ItemQuery(), CancellationToken.None)).Single();
        Assert.Equal(new[] { t1.Id, t2.Id }, listed.Tags.ToArray());

        Assert.True(await items.RemoveTagAsync(id, t2.Id, CancellationToken.None));
        Assert.False(await items.RemoveTagAsync(id, t2.Id, CancellationToken.None));
        loaded = await items.GetByIdAsync(id, CancellationToken.None);
        Assert.Equal(new[] { t1.Id }, loaded!.Tags.ToArray());
    }

    [Fact]
    public async Task AddTag_MissingItemOrTag_ReturnsFalse()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("hello"), CancellationToken.None);
        var tag = await tags.GetOrCreateAsync("x", null, CancellationToken.None);

        Assert.False(await items.AddTagAsync(id + 999, tag.Id, CancellationToken.None));
        Assert.False(await items.AddTagAsync(id, tag.Id + 999, CancellationToken.None));
    }

    [Fact]
    public async Task AddTag_RaisesItemsChangedUpdated()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("hello"), CancellationToken.None);
        var tag = await tags.GetOrCreateAsync("x", null, CancellationToken.None);
        ItemsChangedEventArgs? seen = null;
        items.ItemsChanged += (_, e) => seen = e;

        await items.AddTagAsync(id, tag.Id, CancellationToken.None);

        Assert.Equal(new ItemsChangedEventArgs(ItemsChangeKind.Updated, id), seen);
    }

    [Fact]
    public async Task List_TagFilter_UsesAndSemantics_AcrossCategories()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var both = await items.AddAsync(TextItem("both", category: "Git"), CancellationToken.None);
        var onlyOne = await items.AddAsync(TextItem("only one", category: "Unreal"), CancellationToken.None);
        var onlyTwo = await items.AddAsync(TextItem("only two"), CancellationToken.None);
        await items.AddAsync(TextItem("untagged"), CancellationToken.None);
        var t1 = await tags.GetOrCreateAsync("one", null, CancellationToken.None);
        var t2 = await tags.GetOrCreateAsync("two", null, CancellationToken.None);
        await items.AddTagAsync(both, t1.Id, CancellationToken.None);
        await items.AddTagAsync(both, t2.Id, CancellationToken.None);
        await items.AddTagAsync(onlyOne, t1.Id, CancellationToken.None);
        await items.AddTagAsync(onlyTwo, t2.Id, CancellationToken.None);

        Assert.Equal(new[] { both }, await ListIdsAsync(items, new ItemQuery(TagIds: [t1.Id, t2.Id])));
        Assert.Equal(new[] { both, onlyOne }, await ListIdsAsync(items, new ItemQuery(TagIds: [t1.Id])));
        // Duplicated ids in the filter don't change the meaning.
        Assert.Equal(new[] { both, onlyOne }, await ListIdsAsync(items, new ItemQuery(TagIds: [t1.Id, t1.Id])));
        // Combines with the category filter.
        Assert.Equal(new[] { onlyOne }, await ListIdsAsync(items, new ItemQuery(Category: "Unreal", TagIds: [t1.Id])));
    }

    [Fact]
    public async Task List_TagFilter_MatchAny_UsesOrSemantics()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var both = await items.AddAsync(TextItem("both", category: "Git"), CancellationToken.None);
        var onlyOne = await items.AddAsync(TextItem("only one", category: "Unreal"), CancellationToken.None);
        var onlyTwo = await items.AddAsync(TextItem("only two"), CancellationToken.None);
        await items.AddAsync(TextItem("untagged"), CancellationToken.None);
        var t1 = await tags.GetOrCreateAsync("one", null, CancellationToken.None);
        var t2 = await tags.GetOrCreateAsync("two", null, CancellationToken.None);
        await items.AddTagAsync(both, t1.Id, CancellationToken.None);
        await items.AddTagAsync(both, t2.Id, CancellationToken.None);
        await items.AddTagAsync(onlyOne, t1.Id, CancellationToken.None);
        await items.AddTagAsync(onlyTwo, t2.Id, CancellationToken.None);

        var expected = new[] { both, onlyOne, onlyTwo }.OrderBy(id => id).ToArray();
        Assert.Equal(expected, await ListIdsAsync(items, new ItemQuery(TagIds: [t1.Id, t2.Id], TagMatchAny: true)));
        Assert.Equal(new[] { onlyOne }, await ListIdsAsync(items, new ItemQuery(Category: "Unreal", TagIds: [t1.Id, t2.Id], TagMatchAny: true)));
    }

    [Fact]
    public async Task Search_MatchesTagNames_AndCombinesTermsWithContent()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var mit = await items.AddAsync(TextItem("Permission is hereby granted"), CancellationToken.None);
        var other = await items.AddAsync(TextItem("Permission denied"), CancellationToken.None);
        var tag = await tags.GetOrCreateAsync("licenses", null, CancellationToken.None);
        await items.AddTagAsync(mit, tag.Id, CancellationToken.None);

        Assert.Equal(new[] { mit }, await ListIdsAsync(items, new ItemQuery(Search: "licen")));
        // One term from the content, one from the tag: still AND across columns.
        Assert.Equal(new[] { mit }, await ListIdsAsync(items, new ItemQuery(Search: "permission licenses")));
        Assert.Equal(new[] { mit, other }, await ListIdsAsync(items, new ItemQuery(Search: "permission")));

        await items.RemoveTagAsync(mit, tag.Id, CancellationToken.None);
        Assert.Empty(await ListIdsAsync(items, new ItemQuery(Search: "licenses")));
    }

    [Fact]
    public async Task Rename_ReindexesSearch_AndRejectsNameClash()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("some text"), CancellationToken.None);
        var tag = await tags.GetOrCreateAsync("boilerplate", null, CancellationToken.None);
        var other = await tags.GetOrCreateAsync("regex", null, CancellationToken.None);
        await items.AddTagAsync(id, tag.Id, CancellationToken.None);

        Assert.True(await tags.UpdateAsync(tag.Id, "snippets", "#112233", CancellationToken.None));

        Assert.Empty(await ListIdsAsync(items, new ItemQuery(Search: "boilerplate")));
        Assert.Equal(new[] { id }, await ListIdsAsync(items, new ItemQuery(Search: "snippets")));
        var reloaded = await tags.GetAsync(tag.Id, CancellationToken.None);
        Assert.Equal("snippets", reloaded!.Name);
        Assert.Equal("#112233", reloaded.Color);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tags.UpdateAsync(other.Id, "SNIPPETS", null, CancellationToken.None));
        Assert.False(await tags.UpdateAsync(9999, "ghost", null, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesTagFromEveryItem_AndFromSearch()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var a = await items.AddAsync(TextItem("alpha"), CancellationToken.None);
        var b = await items.AddAsync(TextItem("beta"), CancellationToken.None);
        var doomed = await tags.GetOrCreateAsync("doomed", null, CancellationToken.None);
        var kept = await tags.GetOrCreateAsync("kept", null, CancellationToken.None);
        await items.AddTagAsync(a, doomed.Id, CancellationToken.None);
        await items.AddTagAsync(a, kept.Id, CancellationToken.None);
        await items.AddTagAsync(b, doomed.Id, CancellationToken.None);

        Assert.True(await tags.DeleteAsync(doomed.Id, CancellationToken.None));

        Assert.Equal(new[] { kept.Id }, (await items.GetByIdAsync(a, CancellationToken.None))!.Tags.ToArray());
        Assert.Empty((await items.GetByIdAsync(b, CancellationToken.None))!.Tags);
        Assert.Empty(await ListIdsAsync(items, new ItemQuery(Search: "doomed")));
        Assert.Equal(new[] { a }, await ListIdsAsync(items, new ItemQuery(Search: "kept")));
        Assert.Equal(1, await CountJoinRowsAsync(fx.Database.GetOpenConnection()));
        Assert.False(await tags.DeleteAsync(doomed.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HardDelete_CascadesItemTags()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("bye"), CancellationToken.None);
        var survivor = await items.AddAsync(TextItem("stay"), CancellationToken.None);
        var tag = await tags.GetOrCreateAsync("t", null, CancellationToken.None);
        await items.AddTagAsync(id, tag.Id, CancellationToken.None);
        await items.AddTagAsync(survivor, tag.Id, CancellationToken.None);

        await items.SoftDeleteAsync(id, CancellationToken.None);
        // Soft-deleted items keep their links (a restore brings the tags back) but drop out of the count.
        Assert.Equal(2, await CountJoinRowsAsync(fx.Database.GetOpenConnection()));
        Assert.Equal(1, (await tags.GetAsync(tag.Id, CancellationToken.None))!.ItemCount);

        await items.HardDeleteOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Null(await items.GetByIdAsync(id, CancellationToken.None));
        Assert.Equal(1, await CountJoinRowsAsync(fx.Database.GetOpenConnection()));
        // The FTS index stayed consistent through the cascade: the survivor is still findable by tag.
        Assert.Equal(new[] { survivor }, await ListIdsAsync(items, new ItemQuery(Search: "t")));
    }

    [Fact]
    public async Task List_ReturnsTagsSortedByName_WithLiveCounts()
    {
        await using var fx = await new TempDatabaseFixture().InitializeAsync();
        var (items, tags) = CreateStores(fx);
        var id = await items.AddAsync(TextItem("x"), CancellationToken.None);
        var zeta = await tags.GetOrCreateAsync("zeta", null, CancellationToken.None);
        await tags.GetOrCreateAsync("Alpha", null, CancellationToken.None);
        await items.AddTagAsync(id, zeta.Id, CancellationToken.None);

        var list = await tags.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "zeta" }, list.Select(t => t.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, list.Select(t => t.ItemCount).ToArray());
    }

    [Theory]
    [InlineData(false, 0, 0, true)]     // no retention at all
    [InlineData(false, 10, 0, false)]   // max-count cap
    [InlineData(false, 0, 60, false)]   // time-based cleanup
    [InlineData(true, 10, 60, true)]    // pinned items are exempt from both
    public void IsTaggable_FollowsRetentionRules(bool pinned, int maxItems, int cleanupMinutes, bool expected)
    {
        var category = new Category("C", null, 0, maxItems, cleanupMinutes);
        Assert.Equal(expected, TagRules.IsTaggable(pinned, category));
    }

    [Fact]
    public void IsTaggable_UnknownCategory_IsTaggable()
        => Assert.True(TagRules.IsTaggable(pinned: false, category: null));

    [Theory]
    [InlineData("#a1b2c3", "#A1B2C3")]
    [InlineData("a1b2c3", "#A1B2C3")]
    [InlineData("#abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeColor_AcceptsOnlyRgbHex(string? raw, string? expected)
        => Assert.Equal(expected, TagRules.NormalizeColor(raw));

    [Fact]
    public void NormalizeName_CapsLength()
        => Assert.Equal(TagRules.MaxNameLength, TagRules.NormalizeName(new string('a', 100))!.Length);

    private static async Task<long> CountJoinRowsAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_tags;";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
