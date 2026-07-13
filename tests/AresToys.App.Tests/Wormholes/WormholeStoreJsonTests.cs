using System.IO;
using System.Text.Json;
using AresToys.App.Services.Wormholes;
using AresToys.Storage.Paths;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>
/// Coverage for the layout-presets store: single live positions.json, named preset CRUD, the
/// per-setup auto-apply map, and the one-time migration off the legacy per-monitor-setup
/// Positions\&lt;hash&gt;.json layout.
///
/// Tests use a temp folder + a stub <see cref="IStoragePathResolver"/> so each fact runs against
/// a clean disk state. The store derives the current setup fingerprint from the real monitor
/// enumeration; on a headless CI box that is a stable sentinel ("no-monitors"). We read the same
/// value via <see cref="MonitorSetupIdentifier.ComputeCurrentSetupHash"/> so the assertions stay
/// machine-independent.
/// </summary>
public class WormholeStoreJsonTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StubPaths _paths;

    public WormholeStoreJsonTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AresToys-WormholeStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new StubPaths(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string WormholesDir => Path.Combine(_tempRoot, "Wormholes");

    private async Task<WormholeRecord> SeedRecordAsync(WormholeStoreJson store, double x, double y)
    {
        var rec = new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Title = "R",
            Portal = new PortalWormholeConfig { SourcePath = @"C:\Temp" },
        };
        rec.Geometry.X = x;
        rec.Geometry.Y = y;
        await store.SaveAsync(rec, CancellationToken.None);
        return rec;
    }

    [Fact]
    public async Task SaveAsync_WritesGeometryToPositionsFileNotWormholesJson()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = await SeedRecordAsync(store, 123, 234);

        var wormholesRaw = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "wormholes.json"));
        Assert.DoesNotContain("\"geometry\"", wormholesRaw);
        Assert.Contains("R", wormholesRaw);

        var positionsRaw = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "positions.json"));
        var positions = JsonSerializer.Deserialize<WormholePositionsFile>(positionsRaw, ReadOptions);
        Assert.NotNull(positions);
        Assert.Equal(123, positions!.Positions[rec.Id].X);
    }

    [Fact]
    public async Task SavePreset_CreatesPresetAndAssociatesCurrentSetup()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = await SeedRecordAsync(store, 500, 400);

        await store.SavePresetAsync("Home", CancellationToken.None);

        var names = await store.ListPresetNamesAsync(CancellationToken.None);
        Assert.Contains("Home", names);

        var forSetup = await store.GetPresetNameForCurrentSetupAsync(CancellationToken.None);
        Assert.Equal("Home", forSetup);

        var positions = await store.GetPresetPositionsAsync("Home", CancellationToken.None);
        Assert.NotNull(positions);
        Assert.Equal(500, positions![rec.Id].X);
    }

    [Fact]
    public async Task SavePreset_CapturesHiddenLockedRolledState()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Title = "R",
            Portal = new PortalWormholeConfig { SourcePath = @"C:\Temp" },
            IsHidden = true,
            IsLocked = true,
        };
        await store.SaveAsync(rec, CancellationToken.None);

        await store.SavePresetAsync("Home", CancellationToken.None);

        var states = await store.GetPresetStatesAsync("Home", CancellationToken.None);
        Assert.True(states.ContainsKey(rec.Id));
        Assert.True(states[rec.Id].Hidden);
        Assert.True(states[rec.Id].Locked);
        Assert.False(states[rec.Id].Rolled);
    }

    [Fact]
    public async Task SavePreset_SameName_OverwritesInsteadOfDuplicating()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = await SeedRecordAsync(store, 10, 10);
        await store.SavePresetAsync("Home", CancellationToken.None);

        // Move the wormhole and re-save under the same name.
        rec.Geometry.X = 999;
        await store.SaveAsync(rec, CancellationToken.None);
        await store.SavePresetAsync("Home", CancellationToken.None);

        var names = await store.ListPresetNamesAsync(CancellationToken.None);
        Assert.Single(names, n => string.Equals(n, "Home", StringComparison.OrdinalIgnoreCase));
        var positions = await store.GetPresetPositionsAsync("Home", CancellationToken.None);
        Assert.Equal(999, positions![rec.Id].X);
    }

    [Fact]
    public async Task DeletePreset_RemovesPresetAndSetupMapping()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        await SeedRecordAsync(store, 1, 1);
        await store.SavePresetAsync("Home", CancellationToken.None);

        await store.DeletePresetAsync("Home", CancellationToken.None);

        Assert.Empty(await store.ListPresetNamesAsync(CancellationToken.None));
        Assert.Null(await store.GetPresetNameForCurrentSetupAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RenamePreset_RenamesAndRepointsSetupMapping()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        await SeedRecordAsync(store, 1, 1);
        await store.SavePresetAsync("Home", CancellationToken.None);

        await store.RenamePresetAsync("Home", "Casa", CancellationToken.None);

        var names = await store.ListPresetNamesAsync(CancellationToken.None);
        Assert.Contains("Casa", names);
        Assert.DoesNotContain("Home", names);
        Assert.Equal("Casa", await store.GetPresetNameForCurrentSetupAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RenamePreset_ToExistingName_Throws()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        await SeedRecordAsync(store, 1, 1);
        await store.SavePresetAsync("Home", CancellationToken.None);
        await store.SavePresetAsync("Office", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RenamePresetAsync("Home", "Office", CancellationToken.None));
    }

    [Fact]
    public async Task ApplyPositions_UpdatesRecordGeometryAndPositionsFile()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = await SeedRecordAsync(store, 100, 100);

        var target = new Dictionary<Guid, WormholeGeometry>
        {
            [rec.Id] = new WormholeGeometry { X = 777, Y = 888, Width = 320, Height = 240, UnrolledHeight = 240 },
        };
        await store.ApplyPositionsAsync(target, CancellationToken.None);

        var records = await store.LoadAllAsync(CancellationToken.None);
        Assert.Equal(777, records.Single().Geometry.X);

        var positionsRaw = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "positions.json"));
        var positions = JsonSerializer.Deserialize<WormholePositionsFile>(positionsRaw, ReadOptions);
        Assert.Equal(777, positions!.Positions[rec.Id].X);
    }

    [Fact]
    public async Task SavePreset_EmptyName_Throws()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SavePresetAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task FirstRun_MigratesLegacyPerSetupPositionsIntoPositionsAndOriginalPreset()
    {
        // Stage the legacy layout: wormholes.json (definitions), Positions\<currentHash>.json with
        // geometry, and a .original marker naming that hash. Migration must produce positions.json
        // with the same geometry, seed an "Original" preset, and archive the Positions folder.
        Directory.CreateDirectory(WormholesDir);
        var id = Guid.NewGuid();
        var currentHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();

        var wormholesJson = $$"""
            { "$schema_version": 2, "wormholes": [ { "id": "{{id}}", "title": "L", "portal": { "sourcePath": "C:\\Temp" } } ] }
            """;
        await File.WriteAllTextAsync(Path.Combine(WormholesDir, "wormholes.json"), wormholesJson);

        var positionsDir = Path.Combine(WormholesDir, "Positions");
        Directory.CreateDirectory(positionsDir);
        var legacyPositions = $$"""
            { "$schema_version": 1, "positions": { "{{id}}": { "x": 640, "y": 480, "width": 800, "height": 600, "unrolledHeight": 600 } } }
            """;
        await File.WriteAllTextAsync(Path.Combine(positionsDir, currentHash + ".json"), legacyPositions);
        await File.WriteAllTextAsync(Path.Combine(positionsDir, ".original"), currentHash);

        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        var records = await store.LoadAllAsync(CancellationToken.None);

        // Geometry migrated onto the record.
        Assert.Equal(640, records.Single().Geometry.X);

        // positions.json now exists with the geometry.
        var positionsRaw = await File.ReadAllTextAsync(Path.Combine(WormholesDir, "positions.json"));
        var positions = JsonSerializer.Deserialize<WormholePositionsFile>(positionsRaw, ReadOptions);
        Assert.Equal(640, positions!.Positions[id].X);

        // An "Original" preset was seeded and mapped to this setup.
        var names = await store.ListPresetNamesAsync(CancellationToken.None);
        Assert.Contains("Original", names);
        Assert.Equal("Original", await store.GetPresetNameForCurrentSetupAsync(CancellationToken.None));

        // The legacy Positions folder was archived (renamed aside), not left in place.
        Assert.False(Directory.Exists(positionsDir));
    }

    private sealed class StubPaths(string root) : IStoragePathResolver
    {
        public string ResolveRoot() => root;
        public string ResolveDatabasePath() => Path.Combine(root, "db.sqlite");
        public string ResolveBlobRoot() => Path.Combine(root, "blobs");
    }
}
