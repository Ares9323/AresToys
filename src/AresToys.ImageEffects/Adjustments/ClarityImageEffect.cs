using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Two-pass
/// unsharp-mask variant: blur the source, subtract from original to get the high-frequency
/// detail, then add it back weighted by a midtone Gaussian mask. Emphasises midtone contrast
/// without crushing shadows / highlights.</summary>
public sealed class ClarityImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "clarity";
    public override string Name => "Clarity";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var amount = Math.Clamp(Amount, -100f, 100f) / 100f;
        if (Math.Abs(amount) < 0.001f) return source.Copy();

        int width = source.Width, height = source.Height;
        var blurRadius = Math.Max(Math.Max(width, height) * 0.02f, 3f);

        // SKImageFilter.CreateBlur is GPU-friendly and orders-of-magnitude faster than a
        // hand-rolled box blur — important when chained with other effects in live preview.
        var blurred = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using (var canvas = new SKCanvas(blurred))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blurRadius, blurRadius) })
        {
            canvas.DrawBitmap(source, 0, 0, paint);
        }

        var srcPixels = source.Pixels;
        var blurPixels = blurred.Pixels;
        blurred.Dispose();

        var count = srcPixels.Length;
        var result = new SKColor[count];
        for (var i = 0; i < count; i++)
        {
            var src = srcPixels[i];
            var blur = blurPixels[i];

            float sr = src.Red / 255f, sg = src.Green / 255f, sb = src.Blue / 255f;
            float br = blur.Red / 255f, bg = blur.Green / 255f, bb = blur.Blue / 255f;

            var lum = (0.299f * sr) + (0.587f * sg) + (0.114f * sb);
            var diff = lum - 0.5f;
            var midtoneMask = MathF.Exp(-8f * diff * diff);

            var strength = amount * midtoneMask * 1.5f;
            var outR = Math.Clamp(sr + ((sr - br) * strength), 0f, 1f);
            var outG = Math.Clamp(sg + ((sg - bg) * strength), 0f, 1f);
            var outB = Math.Clamp(sb + ((sb - bb) * strength), 0f, 1f);

            result[i] = new SKColor(
                (byte)MathF.Round(outR * 255f),
                (byte)MathF.Round(outG * 255f),
                (byte)MathF.Round(outB * 255f),
                src.Alpha);
        }

        return new SKBitmap(width, height, source.ColorType, source.AlphaType) { Pixels = result };
    }
}
