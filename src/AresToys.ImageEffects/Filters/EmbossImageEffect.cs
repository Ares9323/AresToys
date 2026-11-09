using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Emboss kernel — gives a stamped/relief look. Anti-symmetric kernel offset so
/// midtones stay grey instead of black.</summary>
public sealed class EmbossImageEffect : FilterImageEffectBase
{
    public override string Id => "emboss";
    public override string Name => "Emboss";

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var k = new float[] { -2, -1, 0, -1, 1, 1, 0, 1, 2 };
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        using var filter = SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(3, 3), k, 1f, 0.5f, new SKPointI(1, 1),
            SKShaderTileMode.Clamp, convolveAlpha: false);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }
}
