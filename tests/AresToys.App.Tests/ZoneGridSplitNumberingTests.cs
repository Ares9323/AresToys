using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public class ZoneGridSplitNumberingTests
{
    private static GridLayout Columns(params int[] cells) =>
        new([10000], Enumerable.Repeat(10000 / cells.Length, cells.Length).ToArray(), [cells], 0);

    [Fact]
    public void Split_InReadingOrder_RenumbersLeftToRight()
    {
        var layout = Columns(0, 1);

        var result = ZoneGridEditor.Split(layout, 0, ZoneAxis.Vertical, 2500);

        Assert.True(result.Changed);
        Assert.Equal([0, 1, 2], result.Layout.CellMap[0]);
    }

    [Fact]
    public void Split_ManualOrder_KeepsTheOtherNumbers()
    {
        var layout = Columns(1, 0);

        var result = ZoneGridEditor.Split(layout, 0, ZoneAxis.Vertical, 7500);

        Assert.True(result.Changed);
        Assert.Equal([1, 0, 2], result.Layout.CellMap[0]);
    }
}
