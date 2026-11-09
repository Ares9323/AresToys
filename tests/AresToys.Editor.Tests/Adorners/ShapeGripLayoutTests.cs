using AresToys.Editor.Adorners;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Adorners;

public class ShapeGripLayoutTests
{
    [Fact]
    public void Rectangle_has_eight_grips_at_corners_and_midedges()
    {
        var r = new RectangleShape(10, 20, 100, 60, ShapeColor.Red, ShapeColor.Transparent, 2);
        var grips = ShapeGripLayout.GripsFor(r);
        Assert.Equal(8, grips.Count);
        Assert.Contains(grips, g => g.Kind == GripKind.TopLeft && g.X == 10 && g.Y == 20);
        Assert.Contains(grips, g => g.Kind == GripKind.BottomRight && g.X == 110 && g.Y == 80);
        Assert.Contains(grips, g => g.Kind == GripKind.Top && g.X == 60 && g.Y == 20);
    }

    [Fact]
    public void Line_has_two_endpoints_plus_bend_grip()
    {
        // LineShape gained a Bend grip alongside its endpoints when we shipped the curved-line
        // feature: dragging the Bend handle pulls the line into a quadratic-Bézier curve via
        // ControlOffsetX/Y. The grip is rendered for every line so users can discover the
        // curve gesture without first selecting a "make curved" toggle.
        var l = new LineShape(0, 0, 100, 100, ShapeColor.Red, 2);
        var grips = ShapeGripLayout.GripsFor(l);
        Assert.Equal(3, grips.Count);
        Assert.Equal(GripKind.From, grips[0].Kind);
        Assert.Equal(GripKind.To, grips[1].Kind);
        Assert.Equal(GripKind.Bend, grips[2].Kind);
    }

    [Fact]
    public void Freehand_has_no_grips()
    {
        var f = new FreehandShape([(0.0, 0.0), (10.0, 10.0)], ShapeColor.Red, 2);
        Assert.Empty(ShapeGripLayout.GripsFor(f));
    }

    [Fact]
    public void HitTest_returns_grip_kind_within_tolerance()
    {
        var r = new RectangleShape(0, 0, 100, 50, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Equal(GripKind.TopLeft, ShapeGripLayout.HitTest(r, 2, 2));
        Assert.Equal(GripKind.BottomRight, ShapeGripLayout.HitTest(r, 98, 48));
        Assert.Equal(GripKind.None, ShapeGripLayout.HitTest(r, 50, 25));
    }
}
