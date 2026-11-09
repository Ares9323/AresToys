using AresToys.Editor.Model;
using Xunit;

namespace AresToys.Editor.Tests.Model;

public class HsvTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 255, 0)]
    [InlineData(0, 255, 255)]
    [InlineData(255, 0, 255)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(128, 64, 200)]
    public void Roundtrip_RGB_to_HSV_and_back_preserves_color(byte r, byte g, byte b)
    {
        var hsv = Hsv.FromRgb(r, g, b);
        var (r2, g2, b2) = hsv.ToRgb();
        Assert.Equal(r, r2);
        Assert.Equal(g, g2);
        Assert.Equal(b, b2);
    }

    [Fact]
    public void Pure_red_has_hue_zero_saturation_one_value_one()
    {
        var hsv = Hsv.FromRgb(255, 0, 0);
        Assert.Equal(0, hsv.H, precision: 4);
        Assert.Equal(1, hsv.S);
        Assert.Equal(1, hsv.V);
    }

    [Fact]
    public void Black_has_value_zero_saturation_zero()
    {
        var hsv = Hsv.FromRgb(0, 0, 0);
        Assert.Equal(0, hsv.V);
        Assert.Equal(0, hsv.S);
    }

    [Fact]
    public void Gray_has_saturation_zero()
    {
        var hsv = Hsv.FromRgb(128, 128, 128);
        Assert.Equal(0, hsv.S, precision: 4);
    }
}
