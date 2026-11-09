using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Adds uniform per-pixel noise. <see cref="Amount"/> controls the maximum jitter
/// applied to each channel.</summary>
public sealed class AddNoiseImageEffect : FilterImageEffectBase
{
    public override string Id => "add_noise";
    public override string Name => "Add noise";

    [EffectParameter(0, 100, DisplayName = "Amount")]
    public float Amount { get; set; } = 20f;

    public bool Monochrome { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Amount <= 0) return source.Copy();
        var rand = Random.Shared;
        var amount = (int)Math.Clamp(Amount, 0, 100) * 255 / 100;
        return AdjustmentImageEffectBaseHelpers.ApplyPixelOperation(source, c =>
        {
            // Per-channel noise (Monochrome=false) or single-shared (Monochrome=true).
            var jitterR = rand.Next(-amount, amount + 1);
            var jitterG = Monochrome ? jitterR : rand.Next(-amount, amount + 1);
            var jitterB = Monochrome ? jitterR : rand.Next(-amount, amount + 1);
            return new SKColor(
                (byte)Math.Clamp(c.Red + jitterR, 0, 255),
                (byte)Math.Clamp(c.Green + jitterG, 0, 255),
                (byte)Math.Clamp(c.Blue + jitterB, 0, 255),
                c.Alpha);
        });
    }
}

/// <summary>Internal helper — exposes <c>ApplyPixelOperation</c> so Filters can reuse the
/// same fast unsafe-pointer iteration that AdjustmentImageEffectBase uses.</summary>
internal static class AdjustmentImageEffectBaseHelpers
{
    public static unsafe SKBitmap ApplyPixelOperation(SKBitmap source, Func<SKColor, SKColor> op)
    {
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        if (source.ColorType == SKColorType.Bgra8888)
        {
            var count = source.Width * source.Height;
            var srcPtr = (SKColor*)source.GetPixels();
            var dstPtr = (SKColor*)result.GetPixels();
            for (var i = 0; i < count; i++) *dstPtr++ = op(*srcPtr++);
        }
        else
        {
            var src = source.Pixels;
            var dst = new SKColor[src.Length];
            for (var i = 0; i < src.Length; i++) dst[i] = op(src[i]);
            result.Pixels = dst;
        }
        return result;
    }
}
