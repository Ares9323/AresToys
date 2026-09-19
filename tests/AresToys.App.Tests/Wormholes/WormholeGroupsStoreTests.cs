using System.IO;
using System.Linq;
using AresToys.App.Services.Wormholes;
using AresToys.Storage.Paths;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Tab groups on disk. The point of putting them in their own <c>groups.json</c> is that
/// grouping stays additive, so these check both halves of that promise: the groups survive a
/// round-trip, and the two pre-existing files are left describing complete, independent wormholes
/// so a build without grouping (or a rollback) loses the tab layout and nothing else.</summary>
public sealed class WormholeGroupsStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StubPaths _paths;

    public WormholeGroupsStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AresToys-WormholeGroupTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new StubPaths(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WormholesDir => Path.Combine(_tempRoot, "Wormholes");
    private WormholeStoreJson NewStore() => new(_paths, NullLogger<WormholeStoreJson>.Instance);

    private static async Task<WormholeRecord> SeedAsync(WormholeStoreJson store, string title, double x)
    {
        var rec = new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Title = title,
            Portal = new PortalWormholeConfig { SourcePath = @"C:\Temp" },
        };
        rec.Geometry.X = x;
        rec.Geometry.Width = 300 + x;
        await store.SaveAsync(rec, CancellationToken.None);
        return rec;
    }

    [Fact]
    public async Task GroupsSurviveARestart()
    {
        Guid a, b;
        using (var store = NewStore())
        {
            a = (await SeedAsync(store, "A", 10)).Id;
            b = (await SeedAsync(store, "B", 20)).Id;
            var groups = await store.LoadGroupsAsync(CancellationToken.None);
            groups.Merge(dragged: b, target: a);
            await store.SaveGroupsAsync(groups, CancellationToken.None);
        }

        using var reopened = NewStore();
        var loaded = await reopened.LoadGroupsAsync(CancellationToken.None);

        var group = Assert.Single(loaded.All);
        Assert.Equal([a, b], group.Members);
        Assert.Equal(a, group.ParentId);
        Assert.Equal(b, group.ActiveId);
    }

    [Fact]
    public async Task GroupingLeavesTheOtherFilesDescribingWholeWormholes()
    {
        using var store = NewStore();
        var a = await SeedAsync(store, "A", 10);
        var b = await SeedAsync(store, "B", 20);

        var groups = await store.LoadGroupsAsync(CancellationToken.None);
        groups.Merge(dragged: b.Id, target: a.Id);
        await store.SaveGroupsAsync(groups, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        // This is the rollback guarantee: both wormholes are still in wormholes.json, and both
        // still own their own geometry in positions.json — including the one that became a tab.
        var definitions = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "wormholes.json"));
        Assert.Contains(a.Id.ToString(), definitions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(b.Id.ToString(), definitions, StringComparison.OrdinalIgnoreCase);

        var positions = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "positions.json"));
        Assert.Contains(a.Id.ToString(), positions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(b.Id.ToString(), positions, StringComparison.OrdinalIgnoreCase);

        // And the grouping itself lives nowhere but its own file.
        Assert.True(File.Exists(Path.Combine(WormholesDir, "groups.json")));
        Assert.DoesNotContain("group", definitions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoGroupsFileMeansNoGroups()
    {
        using var store = NewStore();
        await SeedAsync(store, "A", 10);

        var loaded = await store.LoadGroupsAsync(CancellationToken.None);

        Assert.Empty(loaded.All);
        Assert.False(File.Exists(Path.Combine(WormholesDir, "groups.json")));   // not created on read
    }

    [Fact]
    public async Task AGroupNamingADeletedWormholeIsPrunedOnLoad()
    {
        Guid a, b, c;
        using (var store = NewStore())
        {
            a = (await SeedAsync(store, "A", 10)).Id;
            b = (await SeedAsync(store, "B", 20)).Id;
            c = (await SeedAsync(store, "C", 30)).Id;
            var groups = await store.LoadGroupsAsync(CancellationToken.None);
            groups.Merge(dragged: b, target: a);
            groups.Merge(dragged: c, target: a);
            await store.SaveGroupsAsync(groups, CancellationToken.None);
            await store.DeleteAsync(b, CancellationToken.None);
        }

        using var reopened = NewStore();
        var loaded = await reopened.LoadGroupsAsync(CancellationToken.None);

        var group = Assert.Single(loaded.All);
        Assert.Equal([a, c], group.Members);
    }

    [Fact]
    public async Task AGroupLeftWithOneWormholeDisappearsOnLoad()
    {
        Guid a, b;
        using (var store = NewStore())
        {
            a = (await SeedAsync(store, "A", 10)).Id;
            b = (await SeedAsync(store, "B", 20)).Id;
            var groups = await store.LoadGroupsAsync(CancellationToken.None);
            groups.Merge(dragged: b, target: a);
            await store.SaveGroupsAsync(groups, CancellationToken.None);
            await store.DeleteAsync(b, CancellationToken.None);
        }

        using var reopened = NewStore();
        Assert.Empty((await reopened.LoadGroupsAsync(CancellationToken.None)).All);
    }

    [Fact]
    public async Task AMalformedGroupsFileCostsTheTabLayoutAndNothingElse()
    {
        using (var store = NewStore())
        {
            await SeedAsync(store, "A", 10);
            await SeedAsync(store, "B", 20);
            await store.FlushAsync(CancellationToken.None);
        }
        await File.WriteAllTextAsync(Path.Combine(WormholesDir, "groups.json"), "{ not json at all");

        using var reopened = NewStore();
        var groups = await reopened.LoadGroupsAsync(CancellationToken.None);
        var records = await reopened.LoadAllAsync(CancellationToken.None);

        Assert.Empty(groups.All);
        Assert.Equal(2, records.Count);   // the wormholes themselves are untouched
    }

    /// <summary>Points the store at this test's temp folder. Mirrors the stub in
    /// WormholeStoreJsonTests, which is private to that class.</summary>
    private sealed class StubPaths(string root) : IStoragePathResolver
    {
        public string ResolveRoot() => root;
        public string ResolveDatabasePath() => Path.Combine(root, "db.sqlite");
        public string ResolveBlobRoot() => Path.Combine(root, "blobs");
    }
}
