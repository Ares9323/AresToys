using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Which entries a wormhole hides when "hide service files" is on. These are the files
/// Windows and macOS scatter into folders to store view settings and thumbnail caches: they carry
/// no meaning for the user and just take a tile each in a small wormhole.</summary>
public sealed class WormholeServiceFileFilterTests
{
    [Theory]
    [InlineData(@"C:\Stuff\desktop.ini")]
    [InlineData(@"C:\Stuff\Thumbs.db")]
    [InlineData(@"C:\Stuff\ehthumbs.db")]
    [InlineData(@"C:\Stuff\.DS_Store")]
    public void HidesTheKnownServiceFiles(string path)
    {
        Assert.True(WormholeServiceFileFilter.IsServiceFile(path));
    }

    [Theory]
    // Windows filenames are case-insensitive, and these turn up in every casing there is.
    [InlineData(@"C:\Stuff\DESKTOP.INI")]
    [InlineData(@"C:\Stuff\Desktop.Ini")]
    [InlineData(@"C:\Stuff\thumbs.DB")]
    [InlineData(@"C:\Stuff\.ds_store")]
    public void MatchesRegardlessOfCasing(string path)
    {
        Assert.True(WormholeServiceFileFilter.IsServiceFile(path));
    }

    [Theory]
    [InlineData(@"C:\Stuff\report.docx")]
    [InlineData(@"C:\Stuff\notes.ini")]                  // an .ini that isn't desktop.ini
    [InlineData(@"C:\Stuff\my desktop.ini backup.txt")]  // name merely contains it
    [InlineData(@"C:\Stuff\desktop.ini.bak")]            // different file
    [InlineData(@"C:\Stuff\database.db")]
    [InlineData(@"C:\Stuff\desktop")]                    // no extension
    [InlineData(@"C:\Stuff")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesEverythingElseVisible(string? path)
    {
        Assert.False(WormholeServiceFileFilter.IsServiceFile(path));
    }

    [Fact]
    public void AFolderNamedLikeAServiceFileIsStillHidden()
    {
        // Pathological but harmless: matching is by name, and a folder called "Thumbs.db" is
        // something nobody creates on purpose.
        Assert.True(WormholeServiceFileFilter.IsServiceFile(@"C:\Stuff\Thumbs.db\"));
    }

    [Fact]
    public void FiltersAnEnumerationInPlace()
    {
        string[] entries =
        [
            @"C:\W\desktop.ini",
            @"C:\W\a.txt",
            @"C:\W\Thumbs.db",
            @"C:\W\b.png",
        ];

        Assert.Equal([@"C:\W\a.txt", @"C:\W\b.png"], WormholeServiceFileFilter.Exclude(entries).ToArray());
        // Filter off: the caller gets the sequence back untouched.
        Assert.Equal(entries, WormholeServiceFileFilter.ExcludeIf(entries, hide: false).ToArray());
        Assert.Equal([@"C:\W\a.txt", @"C:\W\b.png"], WormholeServiceFileFilter.ExcludeIf(entries, hide: true).ToArray());
    }
}
