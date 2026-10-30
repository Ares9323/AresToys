using AresToys.App.Views;
using Xunit;

namespace AresToys.App.Tests;

public class PinBlendTests
{
    [Fact]
    public void Multiply_WhiteDropsOut_DarkKeepsItsStrength()
    {
        // Straight BGRA: white, then pure black, then mid grey.
        byte[] pixels = [255, 255, 255, 255, 0, 0, 0, 255, 100, 100, 100, 255];

        PinBlend.Transform(pixels, PinBlendMode.Multiply);

        Assert.Equal(0, pixels[3]);      // white: fully transparent
        Assert.Equal(255, pixels[7]);    // black: fully opaque
        Assert.Equal(155, pixels[11]);   // grey 100: 255 - 100
        Assert.Equal(0, pixels[8]);      // premultiplied colour of the grey is black
    }

    [Fact]
    public void Screen_BlackDropsOut_LightKeepsItsStrength()
    {
        byte[] pixels = [0, 0, 0, 255, 255, 255, 255, 255, 40, 80, 120, 255];

        PinBlend.Transform(pixels, PinBlendMode.Screen);

        Assert.Equal(0, pixels[3]);      // black: fully transparent
        Assert.Equal(255, pixels[7]);    // white: fully opaque
        Assert.Equal(120, pixels[11]);   // alpha follows the strongest channel
        Assert.Equal(120, pixels[10]);   // premultiplied colour stays as it was
    }

    [Fact]
    public void AlreadyTransparentPixels_StayTransparent()
    {
        byte[] pixels = [10, 20, 30, 0];

        PinBlend.Transform(pixels, PinBlendMode.Multiply);

        Assert.Equal(0, pixels[3]);
    }

    [Fact]
    public void Next_CyclesThroughEveryMode()
    {
        var mode = PinBlendMode.Normal;
        mode = PinBlend.Next(mode);
        Assert.Equal(PinBlendMode.Multiply, mode);
        mode = PinBlend.Next(mode);
        Assert.Equal(PinBlendMode.Screen, mode);
        Assert.Equal(PinBlendMode.Normal, PinBlend.Next(mode));
    }

    [Fact]
    public void Clamp_UnknownValueFallsBackToNormal()
        => Assert.Equal(PinBlendMode.Normal, PinBlend.Clamp(99));
}
