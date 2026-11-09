using AresToys.Editor.HitTesting;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.HitTesting;

public class HitTesterAnnotationTests
{
    [Fact]
    public void TextShape_is_hit_inside_box()
    {
        // Box is at (0, 0) sized 100×40; click at (5, 10) lands inside the rect.
        var t = new TextShape(0, 0, 100, 40, "Hello",
            new TextStyle("Segoe UI", 20, false, false, ShapeColor.Red, TextAlign.Left),
            ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.True(ShapeHitTester.IsHit(t, 5, 10));
    }

    [Fact]
    public void TextShape_is_not_hit_far_away()
    {
        var t = new TextShape(0, 0, 100, 40, "Hi", TextStyle.Default, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.False(ShapeHitTester.IsHit(t, 500, 500));
    }

    [Fact]
    public void TextShape_hit_test_follows_box_dimensions()
    {
        // Hit-test ignores text content now — the user resizes the box explicitly via grips
        // and the box defines the hit region. Click anywhere inside the rect grabs the shape.
        var t = new TextShape(0, 0, 80, 50, "ab\nabcdef", TextStyle.Default, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.True(ShapeHitTester.IsHit(t, 40, 30));   // inside the 80×50 box
        Assert.False(ShapeHitTester.IsHit(t, 95, 30));  // past width (80)
        Assert.False(ShapeHitTester.IsHit(t, 40, 60));  // past height (50)
    }

    [Fact]
    public void StepCounter_is_hit_inside_circle()
    {
        var s = new StepCounterShape(100, 100, 1, ShapeColor.Red, ShapeColor.Transparent, 8);
        Assert.True(ShapeHitTester.IsHit(s, 110, 100));
    }

    [Fact]
    public void StepCounter_is_not_hit_outside_circle()
    {
        var s = new StepCounterShape(100, 100, 1, ShapeColor.Red, ShapeColor.Transparent, 8);
        Assert.False(ShapeHitTester.IsHit(s, 200, 100));
    }
}
