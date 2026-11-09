using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Sobel-ish edge detection via 3×3 convolution kernel. Highlights pixels where
/// neighbours differ; flat regions go black.</summary>
public sealed class EdgeDetectImageEffect : FilterImageEffectBase
{
    public override string Id => "edge_detect";
    public override string Name => "Edge detect";

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Laplacian kernel — sums to 0 so flat areas → black.
        var k = new float[] { 0, -1, 0, -1, 4, -1, 0, -1, 0 };
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        using var filter = SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(3, 3), k, 1f, 0f, new SKPointI(1, 1),
            SKShaderTileMode.Clamp, convolveAlpha: false);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }
}
