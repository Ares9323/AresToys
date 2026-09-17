using System.IO;
using System.Text.Json;
using AresToys.App.Services.Wormholes;
using AresToys.Storage.Paths;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>The folder-identity recovery is only worth anything if the identity actually reaches
/// wormholes.json and comes back out intact — the whole point is surviving an app restart, and a
/// 64-bit volume serial is exactly the kind of value a JSON round-trip can quietly mangle.</summary>
public sealed class WormholeSourceIdentityPersistenceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StubPaths _paths;

    public WormholeSourceIdentityPersistenceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AresToys-SourceIdentity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new StubPaths(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task SourceIdentitySurvivesAStoreRestart()
    {
        var id = Guid.NewGuid();
        // ulong.MaxValue-ish serial and a full 128-bit file id: the values most likely to be
        // damaged by a naive round-trip through a JSON number.
        const ulong serial = 0xFEDC_BA98_7654_3210;
        const string fileId = "0123456789ABCDEFFEDCBA9876543210";

        using (var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance))
        {
            await store.SaveAsync(new WormholeRecord
            {
                Id = id,
                Title = "R",
                Portal = new PortalWormholeConfig
                {
                    SourcePath = @"C:\Users\Ares\Documents\Fences\Dev",
                    SourceVolumeSerial = serial,
                    SourceFileId = fileId,
                },
            }, CancellationToken.None);
        }

        using var reopened = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        var record = (await reopened.LoadAllAsync(CancellationToken.None)).Single(r => r.Id == id);

        Assert.Equal(serial, record.Portal.SourceVolumeSerial);
        Assert.Equal(fileId, record.Portal.SourceFileId);
        Assert.True(FolderIdentity.TryParseFileId(record.Portal.SourceFileId, out var high, out var low));
        Assert.Equal(0x0123456789ABCDEFUL, high);
        Assert.Equal(0xFEDCBA9876543210UL, low);
    }

    [Fact]
    public async Task RecordsWrittenBeforeTheFeatureLoadWithAnEmptyIdentity()
    {
        // A wormholes.json from an older build: no identity fields at all.
        var dir = Path.Combine(_tempRoot, "Wormholes");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(dir, "wormholes.json"), $$"""
        {
          "$schema_version": 2,
          "wormholes": [
            { "id": "{{id}}", "title": "Legacy", "portal": { "sourcePath": "C:\\Temp\\Legacy" } }
          ]
        }
        """);

        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        var record = (await store.LoadAllAsync(CancellationToken.None)).Single();

        Assert.Equal(0UL, record.Portal.SourceVolumeSerial);
        Assert.Null(record.Portal.SourceFileId);
        Assert.False(FolderIdentity.TryParseFileId(record.Portal.SourceFileId, out _, out _));
    }

    [Fact]
    public async Task IdentityIsWrittenIntoWormholesJsonItself()
    {
        using var store = new WormholeStoreJson(_paths, NullLogger<WormholeStoreJson>.Instance);
        await store.SaveAsync(new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Portal = new PortalWormholeConfig
            {
                SourcePath = @"C:\Temp",
                SourceVolumeSerial = 42,
                SourceFileId = "00000000000000000000000000000042",
            },
        }, CancellationToken.None);

        var raw = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "Wormholes", "wormholes.json"));
        using var doc = JsonDocument.Parse(raw);
        var portal = doc.RootElement.GetProperty("wormholes")[0].GetProperty("portal");

        Assert.Equal(42UL, portal.GetProperty("sourceVolumeSerial").GetUInt64());
        Assert.Equal("00000000000000000000000000000042", portal.GetProperty("sourceFileId").GetString());
    }

    private sealed class StubPaths(string root) : IStoragePathResolver
    {
        public string ResolveRoot() => root;
        public string ResolveDatabasePath() => Path.Combine(root, "db.sqlite");
        public string ResolveBlobRoot() => Path.Combine(root, "blobs");
    }
}
