using AresToys.App.Services.Wormholes.Favicons;
using Xunit;

namespace AresToys.App.Tests.Wormholes.Favicons;

public class UrlShortcutFileTests
{
    [Fact]
    public void ExtractValue_ReadsUrl_CaseInsensitiveKey()
    {
        var lines = new[] { "[InternetShortcut]", "url=https://example.com/x" };
        Assert.Equal("https://example.com/x", UrlShortcutFile.ExtractValue(lines, "URL"));
    }

    [Fact]
    public void ExtractValue_MissingKey_ReturnsNull()
    {
        var lines = new[] { "[InternetShortcut]", "URL=https://example.com" };
        Assert.Null(UrlShortcutFile.ExtractValue(lines, "IconFile"));
    }

    [Fact]
    public void SetIconLines_ReplacesExistingKeys_PreservesOthers()
    {
        var lines = new[]
        {
            "[InternetShortcut]",
            "URL=https://example.com",
            "IconFile=C:\\old.ico",
            "IconIndex=3",
            "HotKey=0",
        };

        var result = UrlShortcutFile.SetIconLines(lines, "C:\\new.ico", 0);

        Assert.Contains("IconFile=C:\\new.ico", result);
        Assert.Contains("IconIndex=0", result);
        Assert.DoesNotContain("IconFile=C:\\old.ico", result);
        Assert.DoesNotContain("IconIndex=3", result);
        // Untouched keys survive.
        Assert.Contains("URL=https://example.com", result);
        Assert.Contains("HotKey=0", result);
        // Exactly one IconFile / IconIndex line.
        Assert.Single(result, l => l.StartsWith("IconFile=", System.StringComparison.Ordinal));
        Assert.Single(result, l => l.StartsWith("IconIndex=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SetIconLines_AppendsKeysWhenMissing_InsideSection()
    {
        var lines = new[] { "[InternetShortcut]", "URL=https://example.com" };

        var result = UrlShortcutFile.SetIconLines(lines, "C:\\fav.ico", 0);

        Assert.Contains("IconFile=C:\\fav.ico", result);
        Assert.Contains("IconIndex=0", result);
    }

    [Fact]
    public void SetIconLines_KeepsKeysInsideInternetShortcutSection()
    {
        // A trailing section after [InternetShortcut] must not absorb the icon keys.
        var lines = new[]
        {
            "[InternetShortcut]",
            "URL=https://example.com",
            "[OtherSection]",
            "Foo=Bar",
        };

        var result = UrlShortcutFile.SetIconLines(lines, "C:\\fav.ico", 0);

        var iconIdx = IndexOfPrefix(result, "IconFile=");
        var otherIdx = result.ToList().IndexOf("[OtherSection]");
        Assert.True(iconIdx >= 0 && otherIdx >= 0 && iconIdx < otherIdx,
            "IconFile must be written before the next section header");
        Assert.Contains("Foo=Bar", result);
    }

    [Fact]
    public void SetIconLines_IsIdempotent()
    {
        var lines = new[] { "[InternetShortcut]", "URL=https://example.com" };
        var once = UrlShortcutFile.SetIconLines(lines, "C:\\fav.ico", 0);
        var twice = UrlShortcutFile.SetIconLines(once, "C:\\fav.ico", 0);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void SetIconLines_NoSection_CreatesOne()
    {
        var lines = System.Array.Empty<string>();
        var result = UrlShortcutFile.SetIconLines(lines, "C:\\fav.ico", 0);
        Assert.Contains("[InternetShortcut]", result);
        Assert.Contains("IconFile=C:\\fav.ico", result);
        Assert.Contains("IconIndex=0", result);
    }

    private static int IndexOfPrefix(IReadOnlyList<string> lines, string prefix)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith(prefix, System.StringComparison.Ordinal)) return i;
        return -1;
    }
}
