using AresToys.Editor.Adorners;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Adorners;

public class GripDragTests
{
    [Fact]
    public void Rectangle_BottomRight_grip_resizes_keeping_top_left_anchor()
    {
        var r = new RectangleShape(10, 20, 50, 30, ShapeColor.Red, ShapeColor.Transparent, 2);
        var resized = (RectangleShape)GripDrag.Transform(r, GripKind.BottomRight, 100, 200, shiftHeld: false)!;
        Assert.Equal(10, resized.X);
        Assert.Equal(20, resized.Y);
        Assert.Equal(90, resized.Width);
        Assert.Equal(180, resized.Height);
    }

    [Fact]
    public void Rectangle_TopLeft_grip_resizes_keeping_bottom_right_anchor()
    {
        var r = new RectangleShape(10, 20, 50, 30, ShapeColor.Red, ShapeColor.Transparent, 2);
        var resized = (RectangleShape)GripDrag.Transform(r, GripKind.TopLeft, 5, 5, shiftHeld: false)!;
        Assert.Equal(5, resized.X);
        Assert.Equal(5, resized.Y);
        Assert.Equal(55, resized.Width);
        Assert.Equal(45, resized.Height);
    }

    [Fact]
    public void Rectangle_BottomRight_with_Shift_keeps_aspect_ratio_square()
    {
        var r = new RectangleShape(0, 0, 10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        var resized = (RectangleShape)GripDrag.Transform(r, GripKind.BottomRight, 100, 50, shiftHeld: true)!;
        // The larger of the two distances (100) should drive both width and height.
        Assert.Equal(100, resized.Width);
        Assert.Equal(100, resized.Height);
    }

    [Fact]
    public void Rectangle_Top_grip_only_changes_Y_and_Height()
    {
        var r = new RectangleShape(10, 20, 50, 30, ShapeColor.Red, ShapeColor.Transparent, 2);
        var resized = (RectangleShape)GripDrag.Transform(r, GripKind.Top, 999, 5, shiftHeld: false)!;
        Assert.Equal(10, resized.X);
        Assert.Equal(50, resized.Width);    // unchanged
        Assert.Equal(5, resized.Y);
        Assert.Equal(45, resized.Height);   // bottom (50) - new top (5)
    }

    [Fact]
    public void Line_From_grip_moves_only_From_endpoint()
    {
        var l = new LineShape(0, 0, 100, 100, ShapeColor.Red, 2);
        var moved = (LineShape)GripDrag.Transform(l, GripKind.From, 50, 60, shiftHeld: false)!;
        Assert.Equal(50, moved.FromX);
        Assert.Equal(60, moved.FromY);
        Assert.Equal(100, moved.ToX);
        Assert.Equal(100, moved.ToY);
    }

    [Fact]
    public void Arrow_To_grip_with_Shift_snaps_to_45_multiples()
    {
        // Arrow and Line now share the unified LineShape; the EndCap=true gives the "arrow"
        // intent. Same shift-snap logic applies regardless of cap state.
        var a = new LineShape(0, 0, 100, 0, ShapeColor.Red, 2, EndCap: true);
        // Drag To near (10, 10) — ~45° angle. With shift, length is preserved magnitude-wise.
        var moved = (LineShape)GripDrag.Transform(a, GripKind.To, 10, 10, shiftHeld: true)!;
        Assert.Equal(0, moved.FromX);
        Assert.Equal(0, moved.FromY);
        // 45° from origin with length sqrt(200) ≈ 14.14, so cos(45)*len ≈ 10, sin(45)*len ≈ 10.
        Assert.Equal(10, moved.ToX, precision: 4);
        Assert.Equal(10, moved.ToY, precision: 4);
        // EndCap survives the grip transform.
        Assert.True(moved.EndCap);
    }

    [Fact]
    public void Text_BottomRight_grip_resizes_box_not_font()
    {
        // TextShape now behaves like a rectangle frame — grip resize changes Width/Height
        // (text reflows inside) and leaves the font size alone. Drag bottom-right from
        // (100, 40) → (200, 80) doubles each dimension; FontSize stays put.
        var t = new TextShape(0, 0, 100, 40, "ABCDE",
            new TextStyle("Segoe UI", 20, false, false, ShapeColor.Red, TextAlign.Left),
            ShapeColor.Red, ShapeColor.Transparent, 1);
        var resized = (TextShape)GripDrag.Transform(t, GripKind.BottomRight, 200, 80, shiftHeld: false)!;
        Assert.Equal(200, resized.Width);
        Assert.Equal(80, resized.Height);
        Assert.Equal(t.Style.FontSize, resized.Style.FontSize);
    }

    [Fact]
    public void StepCounter_Resize_grip_changes_Radius()
    {
        // Radius derived from StrokeWidth * 10 → stroke=2 yields radius=20 (matches the old
        // explicit Radius arg).
        var c = new StepCounterShape(100, 100, 1, ShapeColor.Red, ShapeColor.Transparent, 2);
        var resized = (StepCounterShape)GripDrag.Transform(c, GripKind.Resize, 200, 200, shiftHeld: false)!;
        Assert.True(resized.Radius > c.Radius);
        Assert.Equal(100, resized.CenterX);
        Assert.Equal(100, resized.CenterY);
    }
}
