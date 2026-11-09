using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public class ZoneSwapTests
{
    [Fact]
    public void Grid_SwapZones_ExchangesTheTwoNumbers()
    {
        var layout = new GridLayout([10000], [3333, 3333, 3334], [[0, 1, 2]], 0);

        var result = ZoneGridEditor.SwapZones(layout, 0, 2);

        Assert.True(result.Changed);
        Assert.Equal([2, 1, 0], result.Layout.CellMap[0]);
        Assert.Null(GridLayout.Validate(result.Layout));
    }

    [Fact]
    public void Grid_SwapZones_RefusesAMissingZoneAndIgnoresTheSameNumber()
    {
        var layout = new GridLayout([10000], [5000, 5000], [[0, 1]], 0);

        Assert.False(ZoneGridEditor.SwapZones(layout, 0, 5).Changed);
        Assert.False(ZoneGridEditor.SwapZones(layout, 1, 1).Changed);
    }

    [Fact]
    public void Grid_SortZones_NumbersLeftToRightThenTopToBottom()
    {
        var layout = new GridLayout([5000, 5000], [5000, 5000], [[3, 1], [2, 0]], 0);

        var result = ZoneGridEditor.SortZones(layout);

        Assert.True(result.Changed);
        Assert.Equal([0, 1], result.Layout.CellMap[0]);
        Assert.Equal([2, 3], result.Layout.CellMap[1]);
        Assert.False(ZoneGridEditor.SortZones(result.Layout).Changed);
    }

    [Fact]
    public void Canvas_SortZones_OrdersByTopThenLeft()
    {
        var low = new CanvasZone(0, 5000, 5000, 10000);
        var right = new CanvasZone(5000, 0, 10000, 5000);
        var left = new CanvasZone(0, 0, 5000, 5000);
        var layout = new CanvasLayout([low, right, left]);

        var result = ZoneCanvasEditor.SortZones(layout);

        Assert.True(result.Changed);
        Assert.Equal([left, right, low], result.Layout.Zones);
        Assert.False(ZoneCanvasEditor.SortZones(result.Layout).Changed);
    }

    [Fact]
    public void Canvas_SwapZones_ExchangesTheEntries()
    {
        var a = new CanvasZone(0, 0, 5000, 5000);
        var b = new CanvasZone(5000, 5000, 10000, 10000);
        var layout = new CanvasLayout([a, b]);

        var result = ZoneCanvasEditor.SwapZones(layout, 0, 1);

        Assert.True(result.Changed);
        Assert.Equal(b, result.Layout.Zones[0]);
        Assert.Equal(a, result.Layout.Zones[1]);
    }
}
