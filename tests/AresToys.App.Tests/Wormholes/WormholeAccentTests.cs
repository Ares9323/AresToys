using System.Windows.Media;
using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Reading and writing a wormhole's accent-colour override. The value is persisted as text
/// in the record, so the parse has to survive whatever ends up in that field: a hand-edited JSON, a
/// value from an older build, or nothing at all.</summary>
public sealed class WormholeAccentTests
{
    [Fact]
    public void AColourRoundTripsThroughTheStoredForm()
    {
        var colour = Color.FromArgb(0xCC, 0x12, 0x34, 0x56);

        var stored = WormholeAccent.Format(colour);
        var parsed = WormholeAccent.TryParse(stored);

        Assert.Equal("#CC123456", stored);
        Assert.Equal(colour, parsed);
    }

    [Theory]
    [InlineData("#FF0000", 0xFF, 0xFF, 0x00, 0x00)]   // no alpha: fully opaque
    [InlineData("#80FF0000", 0x80, 0xFF, 0x00, 0x00)]
    [InlineData("#ff00ff00", 0xFF, 0x00, 0xFF, 0x00)] // lower case
    [InlineData("  #FF0000  ", 0xFF, 0xFF, 0x00, 0x00)]
    public void AcceptsTheShapesAStoredValueCanTake(string stored, byte a, byte r, byte g, byte b)
    {
        Assert.Equal(Color.FromArgb(a, r, g, b), WormholeAccent.TryParse(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]          // named colours aren't what we write, don't invent support
    [InlineData("#XYZ")]
    [InlineData("#FF00")]        // wrong length
    [InlineData("123456")]       // missing the hash
    public void AnythingUnreadableMeansNoOverride(string? stored)
    {
        // Null is the signal to fall back to the theme, so a corrupt value degrades to the default
        // rather than throwing at paint time.
        Assert.Null(WormholeAccent.TryParse(stored));
    }

    [Fact]
    public void ShadingDarkensTowardsBlackWithoutTouchingAlpha()
    {
        var source = Color.FromArgb(0x80, 200, 100, 50);

        var half = WormholeAccent.Shade(source, 0.5);

        Assert.Equal(0x80, half.A);      // opacity is the user's own setting, not ours to change
        Assert.Equal(100, half.R);
        Assert.Equal(50, half.G);
        Assert.Equal(25, half.B);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-5)]    // out of range, clamped
    [InlineData(99)]
    public void ShadingStaysInsideTheColourRangeForAnyAmount(double amount)
    {
        var shaded = WormholeAccent.Shade(Color.FromArgb(0xFF, 255, 255, 255), amount);
        Assert.InRange(shaded.R, 0, 255);
        Assert.InRange(shaded.G, 0, 255);
        Assert.InRange(shaded.B, 0, 255);
    }

    [Fact]
    public void TheBackdropsAreDarkerThanTheRingSoTilesStayLegible()
    {
        var accent = Color.FromArgb(0xFF, 240, 200, 60);

        var body = WormholeAccent.Shade(accent, WormholeAccent.BodyShade);
        var header = WormholeAccent.Shade(accent, WormholeAccent.HeaderShade);

        Assert.True(body.R < accent.R && body.G < accent.G);
        // Header sits above the body and stays distinguishable from it, as the two themed
        // Surface brushes do.
        Assert.True(header.R > body.R);
    }

    [Fact]
    public void TheRingMatchesTheHeaderOnARecolouredWormhole()
    {
        var accent = Color.FromArgb(0xFF, 240, 200, 60);

        // Same shade, so the frame reads as part of the window rather than a brighter outline
        // around it. Tied together in code, and this is what keeps them tied.
        Assert.Equal(
            WormholeAccent.Shade(accent, WormholeAccent.HeaderShade),
            WormholeAccent.Shade(accent, WormholeAccent.RingShade));
    }

    [Fact]
    public void FormatIsStableSoRepeatedSavesDoNotChurnTheRecord()
    {
        var once = WormholeAccent.Format(Color.FromArgb(0xFF, 0xAB, 0xCD, 0xEF));
        Assert.Equal(once, WormholeAccent.Format(WormholeAccent.TryParse(once)!.Value));
    }
}
