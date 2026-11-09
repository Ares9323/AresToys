using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Split toning: tints shadows toward one colour and highlights toward another.
/// Per-pixel luminance picks the blend ratio.</summary>
public sealed class SplitToningImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "split_toning";
    public override string Name => "Split toning";

    public SKColor ShadowColor { get; set; } = new(40, 60, 120);
    public SKColor HighlightColor { get; set; } = new(255, 200, 120);

    [EffectParameter(0, 100, DisplayName = "Balance")]
    public float Balance { get; set; } = 50f;

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 50f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var strength = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (strength <= 0) return source.Copy();
        var balance = Math.Clamp(Balance, 0f, 100f) / 100f;

        return ApplyPixelOperation(source, c =>
        {
            var lum = ((0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue)) / 255f;
            // Smooth ramp around `balance` so the transition isn't a hard threshold.
            var t = Math.Clamp((lum - balance + 0.5f), 0f, 1f);
            var tintR = ShadowColor.Red * (1 - t) + HighlightColor.Red * t;
            var tintG = ShadowColor.Green * (1 - t) + HighlightColor.Green * t;
            var tintB = ShadowColor.Blue * (1 - t) + HighlightColor.Blue * t;
            var r = (byte)Math.Clamp(c.Red * (1 - strength) + tintR * strength, 0, 255);
            var g = (byte)Math.Clamp(c.Green * (1 - strength) + tintG * strength, 0, 255);
            var b = (byte)Math.Clamp(c.Blue * (1 - strength) + tintB * strength, 0, 255);
            return new SKColor(r, g, b, c.Alpha);
        });
    }
}
