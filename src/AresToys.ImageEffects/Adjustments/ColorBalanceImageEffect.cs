using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. 9 sliders
/// — three pairs (cyan-red / magenta-green / yellow-blue) per tonal range
/// (shadows / midtones / highlights). Each pixel's contribution is luminance-weighted so
/// "Shadows: Cyan-Red +50" only tints dark areas.</summary>
public sealed class ColorBalanceImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "color_balance";
    public override string Name => "Color Balance";

    [EffectParameter(-100, 100, DisplayName = "Shadows: Cyan-Red")] public float ShadowCyanRed { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Shadows: Magenta-Green")] public float ShadowMagentaGreen { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Shadows: Yellow-Blue")] public float ShadowYellowBlue { get; set; }

    [EffectParameter(-100, 100, DisplayName = "Mid: Cyan-Red")] public float MidCyanRed { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Mid: Magenta-Green")] public float MidMagentaGreen { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Mid: Yellow-Blue")] public float MidYellowBlue { get; set; }

    [EffectParameter(-100, 100, DisplayName = "Highlights: Cyan-Red")] public float HighlightCyanRed { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Highlights: Magenta-Green")] public float HighlightMagentaGreen { get; set; }
    [EffectParameter(-100, 100, DisplayName = "Highlights: Yellow-Blue")] public float HighlightYellowBlue { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var allZero =
            Math.Abs(ShadowCyanRed) < 0.01f && Math.Abs(ShadowMagentaGreen) < 0.01f && Math.Abs(ShadowYellowBlue) < 0.01f &&
            Math.Abs(MidCyanRed) < 0.01f && Math.Abs(MidMagentaGreen) < 0.01f && Math.Abs(MidYellowBlue) < 0.01f &&
            Math.Abs(HighlightCyanRed) < 0.01f && Math.Abs(HighlightMagentaGreen) < 0.01f && Math.Abs(HighlightYellowBlue) < 0.01f;
        if (allZero) return source.Copy();

        var sCR = ShadowCyanRed / 100f; var sMG = ShadowMagentaGreen / 100f; var sYB = ShadowYellowBlue / 100f;
        var mCR = MidCyanRed / 100f;    var mMG = MidMagentaGreen / 100f;    var mYB = MidYellowBlue / 100f;
        var hCR = HighlightCyanRed / 100f; var hMG = HighlightMagentaGreen / 100f; var hYB = HighlightYellowBlue / 100f;

        return ApplyPixelOperation(source, c =>
        {
            float r = c.Red / 255f, g = c.Green / 255f, b = c.Blue / 255f;
            var lum = (0.299f * r) + (0.587f * g) + (0.114f * b);

            // Three triangular weights summing to 1: shadows peak at lum=0, mids at lum=0.5,
            // highlights at lum=1. Tunable falloff via the *4 / -0.75 constants matches
            // ShareX's tuning.
            var shadowW = Math.Clamp(1f - (lum * 4f), 0f, 1f);
            var highlightW = Math.Clamp((lum - 0.75f) * 4f, 0f, 1f);
            var midW = Math.Max(1f - shadowW - highlightW, 0f);

            var rShift = ((sCR * shadowW) + (mCR * midW) + (hCR * highlightW)) * 0.5f;
            var gShift = ((sMG * shadowW) + (mMG * midW) + (hMG * highlightW)) * 0.5f;
            var bShift = ((sYB * shadowW) + (mYB * midW) + (hYB * highlightW)) * 0.5f;

            r = Math.Clamp(r + rShift, 0f, 1f);
            g = Math.Clamp(g + gShift, 0f, 1f);
            b = Math.Clamp(b + bShift, 0f, 1f);

            return new SKColor(
                (byte)MathF.Round(r * 255f),
                (byte)MathF.Round(g * 255f),
                (byte)MathF.Round(b * 255f),
                c.Alpha);
        });
    }
}
