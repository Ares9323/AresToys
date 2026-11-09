using System.IO;
using AresToys.App.Services.Wormholes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Pins the Windows behaviour the "unlock folders" command exists for: a live
/// <see cref="FolderWatcher"/> holds an open handle inside the folder it watches, and Windows
/// refuses to rename any ANCESTOR of a folder that has an open handle in it. That is exactly what
/// blocks "rename Documents\Fences to Documents\Wormholes" while wormholes are open, and why
/// releasing the watchers is enough to unblock it.</summary>
public sealed class FolderWatcherLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arestoys-watcherlock-" + Guid.NewGuid().ToString("N"));

    public FolderWatcherLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void AWatchedFolderBlocksRenamingItsParent_AndReleasingTheWatcherUnblocksIt()
    {
        var parent = Path.Combine(_root, "Fences");
        var source = Path.Combine(parent, "Dev");
        Directory.CreateDirectory(source);
        var renamed = Path.Combine(_root, "Wormholes");

        var watcher = new FolderWatcher(NullLogger<FolderWatcher>.Instance);
        watcher.Start(source);
        try
        {
            Assert.Equal(source, watcher.WatchedPath);
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, renamed));
        }
        finally
        {
            watcher.Dispose();
        }

        // Same operation, watcher released: this is what PauseWatchers buys the user.
        Directory.Move(parent, renamed);

        Assert.True(Directory.Exists(renamed));
        Assert.True(Directory.Exists(Path.Combine(renamed, "Dev")));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void StopLeavesTheFolderRenameableWithoutDisposingTheWrapper()
    {
        var parent = Path.Combine(_root, "Parent2");
        var source = Path.Combine(parent, "Source");
        Directory.CreateDirectory(source);

        var watcher = new FolderWatcher(NullLogger<FolderWatcher>.Instance);
        watcher.Start(source);
        watcher.Stop();

        Directory.Move(parent, Path.Combine(_root, "Parent2-renamed"));

        Assert.Null(watcher.WatchedPath);
        watcher.Dispose();
    }
}
