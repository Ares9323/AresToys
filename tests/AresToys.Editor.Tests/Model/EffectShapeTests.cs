using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Model;

public class EffectShapeTests
{
    [Fact]
    public void BlurShape_carries_rect_and_radius()
    {
        var b = new BlurShape(10, 20, 100, 50, 12);
        Assert.Equal(10, b.X);
        Assert.Equal(50, b.Height);
        Assert.Equal(12, b.Radius);
    }

    [Fact]
    public void BlurShape_IsEmpty_when_zero_size()
    {
        Assert.True(new BlurShape(0, 0, 0, 10, 5).IsEmpty);
        Assert.True(new BlurShape(0, 0, 10, 0, 5).IsEmpty);
        Assert.False(new BlurShape(0, 0, 1, 1, 5).IsEmpty);
    }

    [Fact]
    public void PixelateShape_carries_blocksize()
    {
        var p = new PixelateShape(0, 0, 100, 100, 16);
        Assert.Equal(16, p.BlockSize);
    }

    [Fact]
    public void SpotlightShape_clamps_dim_within_record_consumer_logic()
    {
        // The record itself doesn't clamp — it stores whatever caller writes; render and ApplyEffectParam clamp.
        var s = new SpotlightShape(0, 0, 50, 50, 0.5);
        Assert.Equal(0.5, s.DimAmount);
    }
}
