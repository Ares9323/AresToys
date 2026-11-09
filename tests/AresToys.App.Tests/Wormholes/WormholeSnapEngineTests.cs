using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

public sealed class WormholeSnapEngineTests
{
    private static readonly SnapRect Work = new(0, 0, 1920, 1040);
    private static readonly SnapRect[] NoNeighbours = [];

    [Fact]
    public void NothingEnabled_LeavesTheRectAlone()
    {
        var r = SnapRect.FromSize(103, 207, 320, 240);
        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, NoNeighbours, Work, new SnapOptions());
        Assert.Equal(r, result);
    }

    [Fact]
    public void Move_SnapsToGridLattice()
    {
        var r = SnapRect.FromSize(103, 207, 320, 240);
        var o = new SnapOptions(ToGrid: true, GridSizePx: 16);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, NoNeighbours, Work, o);

        Assert.Equal(96, result.Left);   // 103 → nearest multiple of 16
        Assert.Equal(208, result.Top);   // 207 → 208
        Assert.Equal(320, result.Width); // a move never resizes
        Assert.Equal(240, result.Height);
    }

    [Fact]
    public void Move_SnapsToScreenEdgeWithGap()
    {
        var r = SnapRect.FromSize(6, 500, 320, 240);
        var o = new SnapOptions(ToScreenEdges: true, GapPx: 8);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, NoNeighbours, Work, o);

        Assert.Equal(8, result.Left);
        Assert.Equal(328, result.Right);
    }

    [Fact]
    public void Move_SnapsRightEdgeToScreenEdge()
    {
        var r = SnapRect.FromSize(1596, 500, 320, 240); // right edge at 1916, 4 px short of 1920
        var o = new SnapOptions(ToScreenEdges: true);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, NoNeighbours, Work, o);

        Assert.Equal(1920, result.Right);
        Assert.Equal(320, result.Width);
    }

    [Fact]
    public void Move_ParksAgainstANeighbourKeepingTheConfiguredGap()
    {
        var neighbour = SnapRect.FromSize(100, 100, 300, 300); // right edge at 400, centre y 250
        var r = SnapRect.FromSize(406, 150, 320, 200);         // 6 px past it, same centre y
        var o = new SnapOptions(ToWormholes: true, GapPx: 10);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, [neighbour], Work, o);

        Assert.Equal(410, result.Left); // neighbour.Right + gap
        Assert.Equal(150, result.Top);  // already centre-aligned, so no vertical correction
    }

    [Fact]
    public void Move_AlignsLeftEdgesOfStackedWormholes()
    {
        var neighbour = SnapRect.FromSize(300, 100, 300, 200);
        var r = SnapRect.FromSize(304, 700, 320, 240); // far below, only alignment applies
        var o = new SnapOptions(ToWormholes: true);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, [neighbour], Work, o);

        Assert.Equal(300, result.Left);
    }

    [Fact]
    public void Move_IgnoresANeighbourFurtherAwayThanTheThreshold()
    {
        var neighbour = SnapRect.FromSize(100, 100, 300, 300);
        var r = SnapRect.FromSize(460, 150, 320, 240); // 60 px past the neighbour's right edge
        var o = new SnapOptions(ToWormholes: true, ThresholdPx: 12);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, [neighbour], Work, o);

        Assert.Equal(460, result.Left);
    }

    [Fact]
    public void Move_NeighbourSnapWinsOverTheGrid()
    {
        var neighbour = SnapRect.FromSize(100, 100, 300, 300); // right edge at 400
        var r = SnapRect.FromSize(404, 150, 320, 240);
        var o = new SnapOptions(ToGrid: true, GridSizePx: 16, ToWormholes: true, GapPx: 6);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, [neighbour], Work, o);

        Assert.Equal(406, result.Left); // 400 + 6, not the 400/416 lattice point
    }

    [Fact]
    public void Resize_OnlyTheDraggedEdgeMoves()
    {
        var r = new SnapRect(500, 500, 1914, 740); // right edge 6 px short of the screen
        var o = new SnapOptions(ToScreenEdges: true);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Right, NoNeighbours, Work, o);

        Assert.Equal(1920, result.Right);
        Assert.Equal(500, result.Left);
        Assert.Equal(500, result.Top);
        Assert.Equal(740, result.Bottom);
    }

    [Fact]
    public void Resize_NeverShrinksBelowTheMinimumSize()
    {
        var neighbour = SnapRect.FromSize(300, 0, 200, 1000); // left edge at 300
        var r = new SnapRect(200, 100, 306, 400);             // dragging the right edge
        var o = new SnapOptions(ToWormholes: true);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Right, [neighbour], Work, o, minWidth: 220);

        Assert.Equal(220, result.Width);
        Assert.Equal(200, result.Left);
    }

    [Fact]
    public void Resize_LeftEdgeSnapsOntoANeighboursRightFlank()
    {
        var neighbour = SnapRect.FromSize(0, 0, 400, 800); // right edge at 400
        var r = new SnapRect(406, 100, 900, 400);
        var o = new SnapOptions(ToWormholes: true, GapPx: 4);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Left, [neighbour], Work, o);

        Assert.Equal(404, result.Left);
        Assert.Equal(900, result.Right);
    }

    [Fact]
    public void LockedGesturesWithNoEdges_AreLeftAlone()
    {
        var r = SnapRect.FromSize(103, 207, 320, 240);
        var o = new SnapOptions(ToGrid: true, GridSizePx: 16, ToScreenEdges: true);

        Assert.Equal(r, WormholeSnapEngine.Snap(r, SnapEdges.None, NoNeighbours, Work, o));
    }

    [Fact]
    public void DetectEdges_SameSizeMeansMove()
    {
        var start = SnapRect.FromSize(100, 100, 300, 200);
        var proposed = SnapRect.FromSize(140, 160, 300, 200);

        Assert.Equal(SnapEdges.Move, WormholeSnapEngine.DetectEdges(start, proposed));
    }

    [Fact]
    public void DetectEdges_FlagsOnlyTheDraggedCorner()
    {
        var start = SnapRect.FromSize(100, 100, 300, 200);
        var proposed = new SnapRect(80, 90, 400, 300); // top-left corner dragged out

        var edges = WormholeSnapEngine.DetectEdges(start, proposed);

        Assert.True(edges.HasFlag(SnapEdges.Left));
        Assert.True(edges.HasFlag(SnapEdges.Top));
        Assert.False(edges.HasFlag(SnapEdges.Move));
    }

    [Fact]
    public void Move_OnAMonitorWithNegativeOrigin_AnchorsTheGridToThatMonitor()
    {
        var leftMonitor = new SnapRect(-1920, 0, 0, 1080);
        var r = SnapRect.FromSize(-1817, 200, 320, 240); // -1817 - (-1920) = 103 into the monitor
        var o = new SnapOptions(ToGrid: true, GridSizePx: 16);

        var result = WormholeSnapEngine.Snap(r, SnapEdges.Move, NoNeighbours, leftMonitor, o);

        Assert.Equal(-1824, result.Left); // -1920 + 96
    }
}
