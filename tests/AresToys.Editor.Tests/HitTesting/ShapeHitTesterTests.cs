using AresToys.Editor.HitTesting;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.HitTesting;

public class ShapeHitTesterTests
{
    private static readonly ShapeColor Red = ShapeColor.Red;
    private static readonly ShapeColor Tx = ShapeColor.Transparent;

    [Fact]
    public void RectangleFill_HitsInsideBody()
    {
        var r = new RectangleShape(10, 10, 100, 50, Red, Red, 2);
        Assert.True(ShapeHitTester.IsHit(r, 50, 30));
    }

    [Fact]
    public void RectangleNoFill_OnlyOutlineBandIsHittable()
    {
        // A transparent-fill rectangle is see-through: only a band around its outline grabs it,
        // inside or outside the box, so a click in the middle reaches what lies beneath.
        var r = new RectangleShape(10, 10, 100, 50, Red, Tx, 2);
        Assert.True(ShapeHitTester.IsHit(r, 14, 30, tolerance: 8));   // just inside the left edge
        Assert.True(ShapeHitTester.IsHit(r, 4, 30, tolerance: 8));    // just outside the left edge
        Assert.False(ShapeHitTester.IsHit(r, 50, 30, tolerance: 8));  // dead centre: miss
        Assert.False(ShapeHitTester.IsHit(r, 200, 30, tolerance: 8)); // far outside: miss
    }

    [Fact]
    public void Line_HitsNearSegment()
    {
        var l = new LineShape(0, 0, 100, 0, Red, 4);
        Assert.True(ShapeHitTester.IsHit(l, 50, 1));
        Assert.False(ShapeHitTester.IsHit(l, 50, 50));
    }

    [Fact]
    public void Ellipse_FilledHitsInside()
    {
        var e = new EllipseShape(0, 0, 100, 50, Red, Red, 2);
        Assert.True(ShapeHitTester.IsHit(e, 50, 25));
    }

    [Fact]
    public void Ellipse_NoFill_OnlyOutlineBandIsHittable()
    {
        // Same rule as the rectangle: a transparent-fill ellipse is grabbed only near its curve.
        var e = new EllipseShape(0, 0, 100, 50, Red, Tx, 2);
        Assert.True(ShapeHitTester.IsHit(e, 100, 25, tolerance: 8));  // on the right vertex
        Assert.True(ShapeHitTester.IsHit(e, 95, 25, tolerance: 8));   // just inside the curve
        Assert.False(ShapeHitTester.IsHit(e, 50, 25, tolerance: 8));  // centre: miss
        Assert.False(ShapeHitTester.IsHit(e, 0, 0, tolerance: 8));    // box corner, outside: miss
    }

    [Fact]
    public void HitTest_TopShapeWins()
    {
        var lower = new RectangleShape(0, 0, 100, 100, Red, Red, 2);
        var upper = new RectangleShape(20, 20, 30, 30, Red, ShapeColor.Black, 2);
        var shapes = new List<Shape> { lower, upper };

        var hit = ShapeHitTester.HitTest(shapes, 30, 30);

        Assert.Equal(upper, hit);
    }
}
