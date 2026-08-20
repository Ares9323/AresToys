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
    public void RectangleNoFill_WholeInteriorIsHittable()
    {
        // A transparent-fill rectangle is grabbable anywhere inside its box (not just on the
        // thin stroke), so a large empty rect can be selected/dragged without pixel-hunting the
        // border. Points outside the box still miss.
        var r = new RectangleShape(10, 10, 100, 50, Red, Tx, 2);
        Assert.True(ShapeHitTester.IsHit(r, 11, 30));   // near the stroke
        Assert.True(ShapeHitTester.IsHit(r, 50, 30));   // dead centre (was a miss before the fix)
        Assert.False(ShapeHitTester.IsHit(r, 200, 30)); // outside → miss
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
    public void Ellipse_NoFill_WholeInteriorIsHittable()
    {
        // Same fix as the rectangle: a transparent-fill ellipse is grabbable anywhere within its
        // ellipse area, not just along the perimeter. Points outside the ellipse still miss.
        var e = new EllipseShape(0, 0, 100, 50, Red, Tx, 2);
        Assert.True(ShapeHitTester.IsHit(e, 100, 25));  // on the right vertex
        Assert.True(ShapeHitTester.IsHit(e, 50, 25));   // centre (was a miss before the fix)
        Assert.False(ShapeHitTester.IsHit(e, 2, 2));    // corner, outside the ellipse → miss
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
