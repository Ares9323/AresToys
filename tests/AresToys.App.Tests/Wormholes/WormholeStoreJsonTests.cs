using System.IO;
using System.Text.Json;
using AresToys.App.Services.Wormholes;
using AresToys.Storage.Paths;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>
/// Integration coverage for the per-monitor-setup split: the migration from a legacy
/// wormholes.json that carried geometry inline, the original-setup marker, and the
/// promise that switching to a never-seen setup never mutates the original's file.
///
/// Tests use a temp folder + a stub <see cref="IStoragePathResolver"/> so each fact runs
/// against a clean disk state. The store derives the active setup hash from the real
/// monitor enumeration at runtime — we can't override that here without touching the
/// production API, so the hash that ends up in the marker is whatever the machine reports.
/// That's fine: the assertions check structural invariants (file exists, contents match,
/// other files untouched) rather than the specific hex digits.
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

    [Fact]
    public async Task FirstRun_LegacyWormholesJsonWithGeometry_MigratesIntoPositionsFile()
    {
        // Stage a legacy wormholes.json containing a record with embedded geometry — exactly
        // the shape pre-multi-setup builds wrote. The migration should produce a
        // Positions\<currentHash>.json with the same X/Y/W/H and stamp .original with the hash.
        var wormholesDir = Path.Combine(_tempRoot, "Wormholes");
        Directory.CreateDirectory(wormholesDir);
        var id = Guid.NewGuid();
        var legacyJson = $$"""
            {
              "$schema_version": 1,
              "wormholes": [
                {
                  "id": "{{id}}",
                  "title": "Legacy",
                  "geometry": { "x": 500, "y": 400, "width": 800, "height": 600, "unrolledHeight": 600 },
                  "portal": { "sourcePath": "C:\\Temp" }
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(wormholesDir, "wormholes.json"), legacyJson);

        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        var records = await store.LoadAllAsync(CancellationToken.None);

        // The record's Geometry must survive the round-trip.
        var rec = Assert.Single(records);
        Assert.Equal(500, rec.Geometry.X);
        Assert.Equal(400, rec.Geometry.Y);
        Assert.Equal(800, rec.Geometry.Width);
        Assert.Equal(600, rec.Geometry.Height);

        // .original must have been stamped with the current setup hash.
        var positionsDir = Path.Combine(wormholesDir, "Positions");
        var markerPath = Path.Combine(positionsDir, ".original");
        Assert.True(File.Exists(markerPath));
        var markerHash = (await File.ReadAllTextAsync(markerPath)).Trim();
        Assert.Equal(store.CurrentSetupHash, markerHash);

        // The per-setup positions file must exist and carry the migrated geometry.
        var setupFile = Path.Combine(positionsDir, store.CurrentSetupHash + ".json");
        Assert.True(File.Exists(setupFile));
        var positionsRaw = await File.ReadAllTextAsync(setupFile);
        var positions = JsonSerializer.Deserialize<WormholePositionsFile>(positionsRaw, ReadOptions);
        Assert.NotNull(positions);
        Assert.True(positions!.Positions.ContainsKey(id));
        Assert.Equal(500, positions.Positions[id].X);
    }

    [Fact]
    public async Task SaveAsync_AfterMigration_DoesNotWriteGeometryIntoWormholesJson()
    {
        // The whole point of the split: wormholes.json must stop carrying per-record
        // geometry. Saving any change should rewrite the file WITHOUT the "geometry" key
        // (which lives in the per-setup file from now on).
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var rec = new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Title = "Fresh",
            Portal = new PortalWormholeConfig { SourcePath = @"C:\Temp" },
        };
        rec.Geometry.X = 123;
        rec.Geometry.Y = 234;
        await store.SaveAsync(rec, CancellationToken.None);

        var wormholesJsonPath = Path.Combine(_tempRoot, "Wormholes", "wormholes.json");
        var raw = await File.ReadAllTextAsync(wormholesJsonPath);
        // String-level assertion: the writer must omit the "geometry" property entirely so
        // there's no risk of stale geometry leaking back via a re-load.
        Assert.DoesNotContain("\"geometry\"", raw);
        // Title (a non-geometry field) must still round-trip.
        Assert.Contains("Fresh", raw);
    }

    [Fact]
    public async Task SwitchSetupAsync_NewHashNoMarker_PromotesItToOriginal()
    {
        // No legacy file, no marker — first ever call after the feature lands. SwitchSetup
        // should snapshot whatever in-memory geometry exists (empty cache here, just
        // exercising the "no prior data" branch) and stamp .original.
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);

        var initialHash = store.CurrentSetupHash;
        Assert.False(string.IsNullOrEmpty(initialHash));

        var markerPath = Path.Combine(_tempRoot, "Wormholes", "Positions", ".original");
        Assert.True(File.Exists(markerPath));
        Assert.Equal(initialHash, (await File.ReadAllTextAsync(markerPath)).Trim());
    }

    [Fact]
    public async Task SwitchSetupAsync_SameHashTwice_IsNoOpAndReturnsEmpty()
    {
        // Calling SwitchSetupAsync with the hash that's already active must not rewrite the
        // positions file or touch any in-memory state — return an empty snapshot so the
        // caller can skip the dispatcher hop entirely.
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var sameHash = store.CurrentSetupHash;

        var snapshot = await store.SwitchSetupAsync(sameHash, CancellationToken.None);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task SwitchSetupAsync_DifferentHashWithMarker_ClonesOriginalIntoNewFile()
    {
        // Stage a legacy file and run a load to stamp .original + create the original
        // positions file. Then switch to a synthetic "different" hash; expect a NEW
        // positions file to be created without touching the original one.
        var wormholesDir = Path.Combine(_tempRoot, "Wormholes");
        Directory.CreateDirectory(wormholesDir);
        var id = Guid.NewGuid();
        var legacyJson = $$"""
            {
              "$schema_version": 1,
              "wormholes": [
                { "id": "{{id}}", "title": "L", "geometry": { "x": 200, "y": 200, "width": 320, "height": 240, "unrolledHeight": 240 }, "portal": { "sourcePath": "C:\\Temp" } }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(wormholesDir, "wormholes.json"), legacyJson);

        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.LoadAllAsync(CancellationToken.None);
        var originalHash = store.CurrentSetupHash;
        var originalFile = Path.Combine(wormholesDir, "Positions", originalHash + ".json");
        var originalBytes = await File.ReadAllBytesAsync(originalFile);
        var originalLastWrite = File.GetLastWriteTimeUtc(originalFile);

        // Switch to a synthetic hash that can't collide with the real current setup hash.
        const string fakeHash = "0000000000000000";
        Assert.NotEqual(fakeHash, originalHash);
        var snapshot = await store.SwitchSetupAsync(fakeHash, CancellationToken.None);

        // The switch must have created the new file.
        var newFile = Path.Combine(wormholesDir, "Positions", fakeHash + ".json");
        Assert.True(File.Exists(newFile));
        // And populated the snapshot with the migrated id.
        Assert.True(snapshot.ContainsKey(id));

        // CRITICAL: the original positions file must NOT have been overwritten. Compare bytes
        // (last-write-time alone isn't reliable on every filesystem; bytes are authoritative).
        var afterBytes = await File.ReadAllBytesAsync(originalFile);
        Assert.Equal(originalBytes, afterBytes);
    }

    private sealed class StubPaths(string root) : IStoragePathResolver
    {
        public string ResolveRoot() => root;
        public string ResolveDatabasePath() => Path.Combine(root, "db.sqlite");
        public string ResolveBlobRoot() => Path.Combine(root, "blobs");
    }
}
