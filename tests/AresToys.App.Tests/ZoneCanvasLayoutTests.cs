using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public class ZoneCanvasLayoutTests
{
    [Fact]
    public void Compute_MapsOverlappingCanvasZonesToPixels()
    {
        var canvas = new CanvasLayout([new CanvasZone(0, 0, 5000, 10000), new CanvasZone(2500, 2500, 7500, 7500)]);
        var layout = ZoneLayout.ForCanvas("c", "Canvas", canvas);
        var area = new PixelRect(100, 50, 1100, 850);

        var zones = ZoneGeometry.Compute(layout, area);

        Assert.Equal(2, zones.Count);
        Assert.Equal(new PixelRect(100, 50, 600, 850), zones[0]);
        Assert.Equal(new PixelRect(350, 250, 850, 650), zones[1]);
    }

    [Fact]
    public void Validate_RejectsBadZonesAndAcceptsValid()
    {
        Assert.NotNull(CanvasLayout.Validate(new CanvasLayout([])));
        Assert.NotNull(CanvasLayout.Validate(new CanvasLayout([new CanvasZone(5000, 0, 5000, 10000)])));
        Assert.NotNull(CanvasLayout.Validate(new CanvasLayout([new CanvasZone(0, 0, 10001, 10000)])));
        Assert.Null(CanvasLayout.Validate(new CanvasLayout([new CanvasZone(0, 0, 10000, 10000), new CanvasZone(100, 100, 300, 300)])));
    }

    [Fact]
    public void Compute_GridThroughLayoutOverloadEqualsGridOverload()
    {
        var grid = new GridLayout([5000, 5000], [3000, 7000], [[0, 1], [2, 1]], 10);
        var layout = new ZoneLayout("g", "Grid", grid);
        var area = new PixelRect(-1920, 0, 0, 1080);

        var viaLayout = ZoneGeometry.Compute(layout, area);
        var viaGrid = ZoneGeometry.Compute(grid, area);

        Assert.Equal(3, viaGrid.Count);
        Assert.Equal(viaGrid, viaLayout);
    }
}
