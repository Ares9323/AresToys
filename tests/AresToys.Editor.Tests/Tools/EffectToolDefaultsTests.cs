using AresToys.Editor.Model;
using AresToys.Editor.Tools;
using Xunit;

namespace AresToys.Editor.Tests.Tools;

/// <summary>Blur / Pixelate / Spotlight used to hard-code their parameters, so every region came
/// out identical. They now read the sticky defaults the wheel and "Set as default" write to.</summary>
public sealed class EffectToolDefaultsTests
{
    private static Shape? Draw(IDrawingTool tool)
    {
        tool.Begin(10, 20, ShapeColor.Red, ShapeColor.Transparent, 2);
        tool.Update(110, 80);
        return tool.Commit(110, 80);
    }

    [Fact]
    public void BlurToolKeepsItsHistoricDefaultRadius()
    {
        var shape = Assert.IsType<BlurShape>(Draw(new BlurTool()));
        Assert.Equal(12, shape.Radius);
    }

    [Fact]
    public void BlurToolUsesTheConfiguredRadius()
    {
        var shape = Assert.IsType<BlurShape>(Draw(new BlurTool { Radius = 37 }));

        Assert.Equal(37, shape.Radius);
        Assert.Equal(10, shape.X);
        Assert.Equal(20, shape.Y);
        Assert.Equal(100, shape.Width);
        Assert.Equal(60, shape.Height);
    }

    [Fact]
    public void PixelateToolKeepsItsHistoricDefaultBlockSize()
        => Assert.Equal(8, Assert.IsType<PixelateShape>(Draw(new PixelateTool())).BlockSize);

    [Fact]
    public void PixelateToolUsesTheConfiguredBlockSize()
        => Assert.Equal(24, Assert.IsType<PixelateShape>(Draw(new PixelateTool { BlockSize = 24 })).BlockSize);

    [Fact]
    public void SpotlightToolKeepsItsHistoricDefaults()
    {
        var shape = Assert.IsType<SpotlightShape>(Draw(new SpotlightTool()));

        Assert.Equal(0.5, shape.DimAmount);
        Assert.Equal(0, shape.BlurRadius); // dim only, as before the blur became adjustable
    }

    [Fact]
    public void SpotlightToolUsesTheConfiguredDimAndBlur()
    {
        var shape = Assert.IsType<SpotlightShape>(Draw(new SpotlightTool { Dim = 0.8, BlurRadius = 15 }));

        Assert.Equal(0.8, shape.DimAmount);
        Assert.Equal(15, shape.BlurRadius);
    }

    [Fact]
    public void ADegenerateDragProducesNothing()
    {
        var tool = new BlurTool { Radius = 20 };
        tool.Begin(50, 50, ShapeColor.Red, ShapeColor.Transparent, 2);
        Assert.Null(tool.Commit(50, 50));
    }
}
