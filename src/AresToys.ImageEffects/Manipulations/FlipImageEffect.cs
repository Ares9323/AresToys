using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Flip horizontally — mirror around the vertical axis.</summary>
public sealed class FlipHorizontalImageEffect : ManipulationImageEffectBase
{
    public override string Id => "flip_horizontal";
    public override string Name => "Flip horizontal";

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Scale(-1, 1, source.Width / 2f, 0);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}

/// <summary>Flip vertically — mirror around the horizontal axis.</summary>
public sealed class FlipVerticalImageEffect : ManipulationImageEffectBase
{
    public override string Id => "flip_vertical";
    public override string Name => "Flip vertical";

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Scale(1, -1, 0, source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
