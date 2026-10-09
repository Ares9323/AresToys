using AresToys.App.Services.Pins;
using Xunit;

namespace AresToys.App.Tests.Pins;

public sealed class PinPlacementTests
{
    // Primary 1920x1080 at the origin, secondary 2560x1440 to its left (negative coordinates).
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Secondary = new(-2560, -200, 2560, 1440);
    private static readonly PixelRect[] TwoMonitors = [Primary, Secondary];

    [Fact]
    public void PinOnSecondaryMonitor_KeepsItsExactPixels()
    {
        var pin = new PixelRect(-1800, 300, 640, 480);
        Assert.Equal(pin, PinPlacement.EnsureVisible(pin, TwoMonitors, Primary));
    }

    [Fact]
    public void PinStraddlingTwoMonitors_StaysPut()
    {
        var pin = new PixelRect(-300, 100, 600, 400);
        Assert.Equal(pin, PinPlacement.EnsureVisible(pin, TwoMonitors, Primary));
    }

    [Fact]
    public void PinMostlyOffScreenButWithA32PixelCornerVisible_StaysPut()
    {
        var pin = new PixelRect(1920 - 32, 1080 - 32, 500, 500);
        Assert.Equal(pin, PinPlacement.EnsureVisible(pin, [Primary], Primary));
    }

    [Fact]
    public void PinWithLessThanTheMinimumVisible_IsCentredOnPrimary()
    {
        var pin = new PixelRect(1920 - 10, 100, 500, 400);
        var placed = PinPlacement.EnsureVisible(pin, [Primary], Primary);
        Assert.Equal(new PixelRect((1920 - 500) / 2, (1080 - 400) / 2, 500, 400), placed);
    }

    [Fact]
    public void PinOnADisconnectedMonitor_IsCentredOnPrimary()
    {
        var pin = new PixelRect(-1800, 300, 640, 480);
        var placed = PinPlacement.EnsureVisible(pin, [Primary], Primary);
        Assert.Equal(new PixelRect(640, 300, 640, 480), placed);
    }

    [Fact]
    public void PinLargerThanPrimary_KeepsItsTopLeftOnScreen()
    {
        var pin = new PixelRect(5000, 5000, 3000, 2000);
        var placed = PinPlacement.EnsureVisible(pin, [Primary], Primary);
        Assert.Equal(new PixelRect(0, 0, 3000, 2000), placed);
    }

    [Fact]
    public void TinyPin_NeedsOnlyItsOwnAreaVisible()
    {
        var pin = new PixelRect(1910, 1070, 10, 10);
        Assert.Equal(pin, PinPlacement.EnsureVisible(pin, [Primary], Primary));
    }

    [Fact]
    public void WithoutPrimary_FallsBackToFirstMonitor()
    {
        var pin = new PixelRect(10000, 0, 100, 100);
        var placed = PinPlacement.EnsureVisible(pin, [Secondary], primary: null);
        Assert.Equal(new PixelRect(-2560 + (2560 - 100) / 2, -200 + (1440 - 100) / 2, 100, 100), placed);
    }

    [Fact]
    public void NoMonitors_ReturnsInputUnchanged()
    {
        var pin = new PixelRect(10000, 0, 100, 100);
        Assert.Equal(pin, PinPlacement.EnsureVisible(pin, [], primary: null));
    }
}
