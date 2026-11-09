using System.IO;
using AresToys.App.Services.Pins;
using AresToys.Storage.Paths;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Pins;

/// <summary>Manifest + PNG store behind "Restore pinned images at startup". Each test runs in
/// its own temp root, deleted on dispose.</summary>
public sealed class PinStoreTests : IDisposable
{
    private readonly string _root;

    public PinStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AresToys-PinStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private PinStore NewStore() => new(new StubPaths(_root), NullLogger<PinStore>.Instance);

    private string PinsDir => Path.Combine(_root, PinStore.FolderName);
    private string ManifestPath => Path.Combine(PinsDir, PinStore.ManifestFileName);

    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static PinRecord ImageRecord(Guid id, string fileName) => new()
    {
        Id = id,
        Kind = PinKind.Image,
        FileName = fileName,
        X = -1500,
        Y = 220,
        Width = 400,
        Height = 300,
        Scale = 1.5,
        Opacity = 0.65,
        Border = 3,
        DpiScaleX = 1.25,
        DpiScaleY = 1.25,
        Locked = true,
    };

    [Fact]
    public void WriteImageAndUpsert_RoundTripsThroughANewInstance()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var name = store.WriteImage(id, s => s.Write(FakePng));
        store.Upsert(ImageRecord(id, name));

        var loaded = NewStore().LoadAndPrune();

        var r = Assert.Single(loaded);
        Assert.Equal(id, r.Id);
        Assert.Equal(PinKind.Image, r.Kind);
        Assert.Equal((-1500, 220, 400, 300), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal(1.5, r.Scale);
        Assert.Equal(0.65, r.Opacity);
        Assert.Equal(3, r.Border);
        Assert.Equal(1.25, r.DpiScaleX);
        Assert.True(r.Locked);
        Assert.Equal(FakePng, File.ReadAllBytes(Path.Combine(PinsDir, name)));
        Assert.Empty(Directory.GetFiles(PinsDir, "*.tmp"));
    }

    [Fact]
    public void Upsert_ReplacesTheEntryWithTheSameId()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var name = store.WriteImage(id, s => s.Write(FakePng));
        store.Upsert(ImageRecord(id, name));
        var moved = ImageRecord(id, name);
        moved.X = 10;
        moved.Scale = 0.5;
        store.Upsert(moved);

        var r = Assert.Single(NewStore().LoadAndPrune());
        Assert.Equal(10, r.X);
        Assert.Equal(0.5, r.Scale);
    }

    [Fact]
    public void Remove_DropsTheEntryAndDeletesItsPng()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var store = NewStore();
        store.Upsert(ImageRecord(keep, store.WriteImage(keep, s => s.Write(FakePng))));
        var dropName = store.WriteImage(drop, s => s.Write(FakePng));
        store.Upsert(ImageRecord(drop, dropName));

        store.Remove(drop);

        Assert.False(File.Exists(Path.Combine(PinsDir, dropName)));
        var r = Assert.Single(NewStore().LoadAndPrune());
        Assert.Equal(keep, r.Id);
    }

    [Fact]
    public void Remove_UnknownIdIsANoOp()
    {
        var store = NewStore();
        store.Remove(Guid.NewGuid());
        Assert.Empty(store.LoadAndPrune());
    }

    [Fact]
    public void LoadAndPrune_DropsEntriesWhoseFileIsMissingAndPersistsThePrune()
    {
        var present = Guid.NewGuid();
        var store = NewStore();
        store.Upsert(ImageRecord(present, store.WriteImage(present, s => s.Write(FakePng))));
        store.Upsert(ImageRecord(Guid.NewGuid(), "gone.png"));
        store.Upsert(new PinRecord { Id = Guid.NewGuid(), Kind = PinKind.Video, SourcePath = Path.Combine(_root, "missing.mp4") });

        var r = Assert.Single(NewStore().LoadAndPrune());
        Assert.Equal(present, r.Id);
        Assert.DoesNotContain("gone.png", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void LoadAndPrune_KeepsVideoEntriesWhoseFileExists()
    {
        var video = Path.Combine(_root, "clip.mp4");
        File.WriteAllBytes(video, [0]);
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Upsert(new PinRecord { Id = id, Kind = PinKind.Video, SourcePath = video, Muted = false });

        var r = Assert.Single(NewStore().LoadAndPrune());
        Assert.Equal(video, r.SourcePath);
        Assert.False(r.Muted);
    }

    [Fact]
    public void LoadAndPrune_DeletesOrphanPngsAndLeftoverTempFiles()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        var name = store.WriteImage(id, s => s.Write(FakePng));
        store.Upsert(ImageRecord(id, name));
        var orphan = Path.Combine(PinsDir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(orphan, FakePng);
        var tmp = Path.Combine(PinsDir, "pins.json.tmp");
        File.WriteAllText(tmp, "{");

        NewStore().LoadAndPrune();

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(tmp));
        Assert.True(File.Exists(Path.Combine(PinsDir, name)));
    }

    [Fact]
    public void LoadAndPrune_RejectsFileNamesThatLeaveThePinsFolder()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), FakePng);
        var store = NewStore();
        store.Upsert(ImageRecord(Guid.NewGuid(), @"..\outside.png"));

        Assert.Empty(NewStore().LoadAndPrune());
        Assert.True(File.Exists(Path.Combine(_root, "outside.png")));
    }

    [Fact]
    public void CorruptManifest_IsRenamedAsideAndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(PinsDir);
        File.WriteAllText(ManifestPath, "{ this is not json");

        var store = NewStore();
        Assert.Empty(store.LoadAndPrune());
        Assert.Single(Directory.GetFiles(PinsDir, PinStore.ManifestFileName + ".corrupt-*"));

        // The store keeps working after the reset.
        var id = Guid.NewGuid();
        store.Upsert(ImageRecord(id, store.WriteImage(id, s => s.Write(FakePng))));
        Assert.Single(NewStore().LoadAndPrune());
    }

    [Fact]
    public void Clear_RemovesManifestAndImages()
    {
        var id = Guid.NewGuid();
        var store = NewStore();
        store.Upsert(ImageRecord(id, store.WriteImage(id, s => s.Write(FakePng))));

        store.Clear();

        Assert.False(File.Exists(ManifestPath));
        Assert.Empty(Directory.GetFiles(PinsDir, "*.png"));
        Assert.Empty(NewStore().LoadAndPrune());
    }

    [Fact]
    public void WriteImage_FailingEncoderLeavesNoFileBehind()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.WriteImage(Guid.NewGuid(), _ => throw new InvalidOperationException("encode failed")));
        Assert.Empty(Directory.GetFiles(PinsDir));
    }

    private sealed class StubPaths(string root) : IStoragePathResolver
    {
        public string ResolveRoot() => root;
        public string ResolveDatabasePath() => Path.Combine(root, "arestoys.db");
        public string ResolveBlobRoot() => Path.Combine(root, "blobs");
    }
}
