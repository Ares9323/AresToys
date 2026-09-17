using System.IO;
using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Exercises the real Win32 file-id APIs against real folders on the test machine's
/// temp volume. Skipped rather than failed when that volume doesn't support file ids (the
/// resolver is designed to degrade to "ask the user", so a FAT-formatted temp dir is a valid
/// environment, just not one that can prove anything).</summary>
public sealed class FolderIdentityResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arestoys-folderid-" + Guid.NewGuid().ToString("N"));

    public FolderIdentityResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Capture_ReturnsAStableIdentityForTheSameFolder()
    {
        var folder = NewFolder("stable");
        var first = FolderIdentityResolver.Capture(folder);
        if (first is null) return; // volume without file-id support

        var second = FolderIdentityResolver.Capture(folder);

        Assert.Equal(first, second);
        Assert.True(first.Value.IsValid);
    }

    [Fact]
    public void Capture_GivesDifferentIdentitiesToDifferentFolders()
    {
        var a = FolderIdentityResolver.Capture(NewFolder("a"));
        var b = FolderIdentityResolver.Capture(NewFolder("b"));
        if (a is null || b is null) return;

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Capture_ReturnsNullForAMissingFolder()
    {
        Assert.Null(FolderIdentityResolver.Capture(Path.Combine(_root, "does-not-exist")));
        Assert.Null(FolderIdentityResolver.Capture(""));
        Assert.Null(FolderIdentityResolver.Capture(null));
    }

    /// <summary>The headline case: the user renames the PARENT of every wormhole source
    /// (Documents\Fences → Documents\Wormholes) and the sources have to be found again.</summary>
    [Fact]
    public void ResolvePath_FindsTheFolderAfterItsParentIsRenamed()
    {
        var parent = NewFolder("Fences");
        var source = Path.Combine(parent, "Dev");
        Directory.CreateDirectory(source);

        var identity = FolderIdentityResolver.Capture(source);
        if (identity is null) return;

        var renamedParent = Path.Combine(_root, "Wormholes");
        Directory.Move(parent, renamedParent);

        var resolved = FolderIdentityResolver.ResolvePath(identity.Value);

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(renamedParent, "Dev")).TrimEnd('\\'),
            Path.GetFullPath(resolved!).TrimEnd('\\'));
    }

    [Fact]
    public void ResolvePath_FindsTheFolderAfterItIsMovedElsewhereOnTheSameVolume()
    {
        var source = NewFolder("Tools");
        var identity = FolderIdentityResolver.Capture(source);
        if (identity is null) return;

        var newHome = NewFolder("Archive");
        var moved = Path.Combine(newHome, "Tools-renamed");
        Directory.Move(source, moved);

        var resolved = FolderIdentityResolver.ResolvePath(identity.Value);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(moved).TrimEnd('\\'), Path.GetFullPath(resolved!).TrimEnd('\\'));
    }

    [Fact]
    public void ResolvePath_ReturnsNullOnceTheFolderIsDeleted()
    {
        var folder = NewFolder("doomed");
        var identity = FolderIdentityResolver.Capture(folder);
        if (identity is null) return;

        Directory.Delete(folder);

        Assert.Null(FolderIdentityResolver.ResolvePath(identity.Value));
    }

    [Fact]
    public void ResolvePath_ReturnsNullForAnEmptyIdentity()
        => Assert.Null(FolderIdentityResolver.ResolvePath(default));

    [Fact]
    public void Matches_DistinguishesARecreatedFolderFromTheOriginal()
    {
        var folder = NewFolder("recreated");
        var identity = FolderIdentityResolver.Capture(folder);
        if (identity is null) return;

        Assert.True(FolderIdentityResolver.Matches(folder, identity.Value));

        Directory.Delete(folder);
        Directory.CreateDirectory(folder); // same path, new folder

        Assert.False(FolderIdentityResolver.Matches(folder, identity.Value));
    }

    [Fact]
    public void FileIdString_RoundTrips()
    {
        var identity = new FolderIdentity(0x1234_5678_9ABC_DEF0, 0x0FED_CBA9_8765_4321, 0xAABB_CCDD_EEFF_0011);

        Assert.True(FolderIdentity.TryParseFileId(identity.ToFileIdString(), out var high, out var low));
        Assert.Equal(identity.FileIdHigh, high);
        Assert.Equal(identity.FileIdLow, low);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothex")]
    [InlineData("0123456789ABCDEF")] // 16 chars — half an id
    public void FileIdString_RejectsMalformedValues(string? text)
        => Assert.False(FolderIdentity.TryParseFileId(text, out _, out _));

    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
