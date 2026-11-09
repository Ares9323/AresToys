using AresToys.Editor.Adorners;
using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Adorners;

public class EffectGripDragTests
{
    [Fact]
    public void BlurShape_BottomRight_grip_resizes()
    {
        var b = new BlurShape(10, 20, 50, 30, 12);
        var resized = (BlurShape)GripDrag.Transform(b, GripKind.BottomRight, 100, 200, shiftHeld: false)!;
        Assert.Equal(10, resized.X);
        Assert.Equal(20, resized.Y);
        Assert.Equal(90, resized.Width);
        Assert.Equal(180, resized.Height);
        Assert.Equal(12, resized.Radius); // radius unchanged by resize
    }

    [Fact]
    public void PixelateShape_TopLeft_grip_resizes()
    {
        var p = new PixelateShape(10, 20, 50, 30, 8);
        var resized = (PixelateShape)GripDrag.Transform(p, GripKind.TopLeft, 0, 0, shiftHeld: false)!;
        Assert.Equal(0, resized.X);
        Assert.Equal(0, resized.Y);
        Assert.Equal(60, resized.Width);
        Assert.Equal(50, resized.Height);
        Assert.Equal(8, resized.BlockSize);
    }

    [Fact]
    public void SpotlightShape_Right_grip_resizes_only_width()
    {
        var s = new SpotlightShape(10, 20, 50, 30, 0.5);
        var resized = (SpotlightShape)GripDrag.Transform(s, GripKind.Right, 200, 999, shiftHeld: false)!;
        Assert.Equal(10, resized.X);
        Assert.Equal(20, resized.Y);
        Assert.Equal(190, resized.Width);
        Assert.Equal(30, resized.Height);
    }
}
