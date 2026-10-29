using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public class ZoneCanvasEditorTests
{
    [Fact]
    public void FromGrid_ThreeColumns_GivesOneFullHeightZonePerColumn()
    {
        var canvas = ZoneCanvasEditor.FromGrid(ZoneLayoutPresets.Columns(3));

        Assert.Equal(3, canvas.ZoneCount);
        Assert.All(canvas.Zones, z => Assert.Equal((0, 10000), (z.Top, z.Bottom)));
        Assert.Equal(0, canvas.Zones[0].Left);
        Assert.Equal(10000, canvas.Zones[2].Right);
        Assert.Equal(canvas.Zones[0].Right, canvas.Zones[1].Left);
        Assert.Equal(canvas.Zones[1].Right, canvas.Zones[2].Left);
    }

    [Fact]
    public void AddZone_NormalizesReversedDragAndRefusesTinyZone()
    {
        var layout = new CanvasLayout([new CanvasZone(0, 0, 5000, 5000)]);

        var added = ZoneCanvasEditor.AddZone(layout, 6000, 7000, 2000, 1000);

        Assert.True(added.Changed);
        Assert.Null(added.Reason);
        Assert.Equal(2, added.Layout.ZoneCount);
        Assert.Equal(new CanvasZone(2000, 1000, 6000, 7000), added.Layout.Zones[1]);

        var tiny = ZoneCanvasEditor.AddZone(layout, 100, 100, 150, 900);

        Assert.False(tiny.Changed);
        Assert.Equal("That zone is too small.", tiny.Reason);
        Assert.Same(layout, tiny.Layout);
    }

    [Fact]
    public void SetZoneEdges_RefusesZoneNarrowerThanMinSize()
    {
        var layout = new CanvasLayout([new CanvasZone(0, 0, 5000, 5000)]);

        var result = ZoneCanvasEditor.SetZoneEdges(layout, 0, left: 4950, top: null, right: null, bottom: null);

        Assert.False(result.Changed);
        Assert.Equal("That zone is too small.", result.Reason);
        Assert.Same(layout, result.Layout);
    }

    [Fact]
    public void RemoveZone_RefusesLastZoneAndRenumbersOtherwise()
    {
        var single = new CanvasLayout([new CanvasZone(0, 0, 10000, 10000)]);
        var refused = ZoneCanvasEditor.RemoveZone(single, 0);

        Assert.False(refused.Changed);
        Assert.Equal("A layout needs at least one zone.", refused.Reason);

        var two = new CanvasLayout([new CanvasZone(0, 0, 5000, 10000), new CanvasZone(5000, 0, 10000, 10000)]);
        var removed = ZoneCanvasEditor.RemoveZone(two, 0);

        Assert.True(removed.Changed);
        Assert.Equal(1, removed.Layout.ZoneCount);
        Assert.Equal(new CanvasZone(5000, 0, 10000, 10000), removed.Layout.Zones[0]);
    }

    [Fact]
    public void History_UndoRedo_IgnoresPushOfEqualState()
    {
        var start = new CanvasLayout([new CanvasZone(0, 0, 10000, 10000)]);
        var history = new ZoneEditHistory<CanvasLayout>(start, ZoneCanvasEditor.SameLayout);
        var added = ZoneCanvasEditor.AddZone(start, 0, 0, 5000, 5000).Layout;

        history.Push(added);
        history.Push(new CanvasLayout(added.Zones.ToList(), added.SensitivityRadius));

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Same(start, history.Undo());
        Assert.False(history.CanUndo);
        Assert.Same(added, history.Redo());
        Assert.False(history.CanRedo);
    }
}
