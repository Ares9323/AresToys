using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Unsharp-mask sharpening: source minus blurred copy added back to source. Skia's
/// 3×3 convolution kernel does the same in one pass.</summary>
public sealed class SharpenImageEffect : FilterImageEffectBase
{
    public override string Id => "sharpen";
    public override string Name => "Sharpen";

    [EffectParameter(0, 100, DisplayName = "Amount")]
    public float Amount { get; set; } = 30f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var amount = Math.Clamp(Amount, 0f, 100f) / 100f;
        if (amount <= 0) return source.Copy();
        // Standard 3x3 sharpen kernel scaled by amount. Centre weight = 1 + 4*amount; edges
        // = -amount; corners = 0. Sums to 1 so brightness stays roughly constant.
        var k = new float[]
        {
            0,       -amount,         0,
            -amount, 1f + 4 * amount, -amount,
            0,       -amount,         0,
        };
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
