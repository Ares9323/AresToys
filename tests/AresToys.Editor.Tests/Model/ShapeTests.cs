using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Model;

public class ShapeTests
{
    [Fact]
    public void RectangleShape_RecordEquality_ComparesByValue()
    {
        var a = new RectangleShape(0, 0, 10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        var b = new RectangleShape(0, 0, 10, 10, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void RectangleShape_IsEmpty_WhenWidthOrHeightNonPositive(double w, double h)
    {
        var s = new RectangleShape(0, 0, w, h, ShapeColor.Red, ShapeColor.Transparent, 1);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void ShapeColor_TransparentSentinel_HasZeroAlpha()
    {
        Assert.True(ShapeColor.Transparent.IsTransparent);
        Assert.Equal(0, ShapeColor.Transparent.A);
    }
}
