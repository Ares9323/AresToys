using AresToys.Editor.Adorners;
using AresToys.Editor.HitTesting;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Adorners;

public class RotationTests
{
    [Fact]
    public void RectangleShape_default_rotation_is_zero()
    {
        var r = new RectangleShape(0, 0, 10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Equal(0, r.Rotation);
    }

    [Fact]
    public void Rotated_rect_hit_test_passes_after_unrotating_click()
    {
        // 100×40 rect at (0,0), rotated 90° around its center (50, 20).
        // After rotation the rect occupies roughly (30, -30) to (70, 70) in canvas space.
        var r = new RectangleShape(0, 0, 100, 40, ShapeColor.Red, ShapeColor.Red, 1, Rotation: 90);
        // (50, 50) should hit (it's inside the rotated rect, well within the rotated area).
        Assert.True(ShapeHitTester.IsHit(r, 50, 50));
        // (90, 0) should miss (it's outside both the original and rotated bbox).
        Assert.False(ShapeHitTester.IsHit(r, 90, 0));
    }

    [Fact]
    public void Rotate_grip_drag_sets_Rotation_to_atan2_plus_90()
    {
        // Rect centered at (50, 50). Drag rotate grip to (100, 50) — directly to the right of center.
        // atan2(0, 50) = 0°, +90° offset = 90°.
        var r = new RectangleShape(0, 0, 100, 100, ShapeColor.Red, ShapeColor.Transparent, 2);
        var rotated = (RectangleShape)GripDrag.Transform(r, GripKind.Rotate, 100, 50, shiftHeld: false)!;
        Assert.Equal(90, rotated.Rotation, precision: 4);
    }

    [Fact]
    public void Rotate_grip_drag_with_Shift_snaps_to_15_degree_increments()
    {
        var r = new RectangleShape(0, 0, 100, 100, ShapeColor.Red, ShapeColor.Transparent, 2);
        // Center is (50, 50). For target rotation = 100°: atan2(dy, dx) = 10°, so
        // dx = cos(10°) * length, dy = sin(10°) * length. Drag point (50 + 9.848, 50 + 1.736).
        // Shift snaps round(100/15)*15 = 105°.
        var rotated = (RectangleShape)GripDrag.Transform(r, GripKind.Rotate, 59.848, 51.736, shiftHeld: true)!;
        Assert.Equal(105, rotated.Rotation, precision: 1);
    }

    [Fact]
    public void Pivot_of_rect_is_geometric_center()
    {
        var r = new RectangleShape(10, 20, 100, 60, ShapeColor.Red, ShapeColor.Transparent, 2);
        var (cx, cy) = ShapeGripLayout.PivotOf(r);
        Assert.Equal(60, cx);
        Assert.Equal(50, cy);
    }
}
