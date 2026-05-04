using SkiaSharp;

namespace ShareQ.ImageEffects.Adjustments;

/// <summary>Shared helpers for colour-matrix / colour-table adjustments. Ported from ShareX
/// (GPL v3) — see ShareX.ImageEditor/Core/ImageEffects/Adjustments/AdjustmentImageEffectBase.cs.</summary>
public abstract class AdjustmentImageEffectBase : ImageEffect
{
    public sealed override ImageEffectCategory Category => ImageEffectCategory.Adjustments;

    protected static SKBitmap ApplyColorMatrix(SKBitmap source, float[] matrix)
    {
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        return ApplyColorFilter(source, filter);
    }

    protected static SKBitmap ApplyColorFilter(SKBitmap source, SKColorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }

    /// <summary>Per-pixel transform fallback for effects that don't have a clean
    /// SKColorFilter expression (gamma, levels, threshold). Iterates the BGRA byte buffer
    /// in-place when the source is 8-bit BGRA — far faster than going through SKBitmap.Pixels
    /// which allocates a managed SKColor[] copy. Ported from ShareX (GPL v3).</summary>
    protected static unsafe SKBitmap ApplyPixelOperation(SKBitmap source, Func<SKColor, SKColor> operation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operation);

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);

        if (source.ColorType == SKColorType.Bgra8888)
        {
            var count = source.Width * source.Height;
            var srcPtr = (SKColor*)source.GetPixels();
            var dstPtr = (SKColor*)result.GetPixels();
            for (var i = 0; i < count; i++)
            {
                *dstPtr++ = operation(*srcPtr++);
            }
        }
        else
        {
            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];
            for (var i = 0; i < srcPixels.Length; i++)
            {
                dstPixels[i] = operation(srcPixels[i]);
            }
            result.Pixels = dstPixels;
        }

        return result;
    }
}
