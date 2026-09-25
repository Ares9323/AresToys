using AresToys.Pipeline.Tasks;
using Xunit;

namespace AresToys.Pipeline.Tests.Tasks;

public class CaptureFileNamerTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 23, 4, 41, 18);

    [Fact]
    public void Defaults_PrefixDateTimeApp()
    {
        Assert.Equal("arestoys-20260925-230441_firefox",
            CaptureFileNamer.Build(null, null, Now, "PA360 — Mozilla Firefox", "firefox"));
    }

    [Fact]
    public void MissingAppName_DoesNotLeaveTrailingSeparator()
    {
        Assert.Equal("arestoys-20260925-230441", CaptureFileNamer.Build(null, null, Now, null, null));
    }

    [Fact]
    public void MissingTitle_DoesNotLeaveDoubleDash()
    {
        Assert.Equal("arestoys-20260925", CaptureFileNamer.Build(null, "%title-%y%mo%d", Now, null, null));
    }

    [Fact]
    public void CustomPrefixAndAppName()
    {
        Assert.Equal("shot-firefox-2026-09-25",
            CaptureFileNamer.Build("shot", "%appName-%y-%mo-%d", Now, "Whatever", "firefox"));
    }

    [Fact]
    public void EmptyPrefix_MeansNoPrefix()
    {
        Assert.Equal("firefox_230441", CaptureFileNamer.Build("", "%appName_%h%mi%s", Now, null, "firefox"));
    }

    [Fact]
    public void TitleContainingTokens_StaysLiteral()
    {
        Assert.Equal("x-100%d", CaptureFileNamer.Build("x", "%title", Now, "100%d", null));
    }

    [Fact]
    public void InvalidCharacters_AreDropped()
    {
        Assert.Equal("ab-cd", CaptureFileNamer.Build("a:b", "c?d", Now, null, null));
    }
}
