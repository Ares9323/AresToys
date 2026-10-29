using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public class ZoneHitTesterTests
{
    private static readonly PixelRect[] TwoColumns = [new PixelRect(0, 0, 100, 100), new PixelRect(100, 0, 200, 100)];

    [Fact]
    public void NearTheLineBetweenTwoZones_BothAreReturned()
    {
        // 95 is inside zone 0 but within the radius (20) of zone 1: PowerToys snaps to both.
        var hits = ZoneHitTester.ZonesFromPoint(TwoColumns, 95, 50, 20);

        Assert.Equal([0, 1], hits);
    }

    [Fact]
    public void FarFromTheLine_OnlyTheContainingZoneIsReturned()
    {
        var hits = ZoneHitTester.ZonesFromPoint(TwoColumns, 40, 50, 20);

        Assert.Equal([0], hits);
    }

    [Fact]
    public void OverlappingZones_OneIsChosen()
    {
        PixelRect[] overlapping = [new PixelRect(0, 0, 100, 100), new PixelRect(20, 20, 120, 120)];

        var hits = ZoneHitTester.ZonesFromPoint(overlapping, 60, 60, 10);

        Assert.Single(hits);
    }

    [Fact]
    public void LoneZoneReachedOnlyThroughTheMargin_IsNotCaptured()
    {
        var hits = ZoneHitTester.ZonesFromPoint([new PixelRect(0, 0, 100, 100)], 110, 50, 20);

        Assert.Empty(hits);
    }
}
