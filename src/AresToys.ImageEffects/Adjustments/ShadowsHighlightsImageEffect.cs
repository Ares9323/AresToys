using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.
/// Positive Shadows brightens dark areas; positive Highlights darkens bright areas. Per-pixel
/// luminance-weighted so the adjustment fades at the opposite end of the tonal range.</summary>
public sealed class ShadowsHighlightsImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "shadows_highlights";
    public override string Name => "Shadows / Highlights";

    [EffectParameter(-100, 100, DisplayName = "Shadows")]
    public float Shadows { get; set; }

    [EffectParameter(-100, 100, DisplayName = "Highlights")]
    public float Highlights { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var shadows = Math.Clamp(Shadows, -100f, 100f);
        var highlights = Math.Clamp(Highlights, -100f, 100f);
        if (Math.Abs(shadows) < 0.0001f && Math.Abs(highlights) < 0.0001f) return source.Copy();

        var sStrength = shadows / 100f;
        var hStrength = highlights / 100f;

        return ApplyPixelOperation(source, c =>
        {
            var luma = ((0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue)) / 255f;
            var shadowWeight = (1f - luma) * (1f - luma);
            var highlightWeight = luma * luma;
            var delta = ((sStrength * shadowWeight) - (hStrength * highlightWeight)) * 255f;
            return new SKColor(
                ClampToByte(c.Red + delta),
                ClampToByte(c.Green + delta),
                ClampToByte(c.Blue + delta),
                c.Alpha);
        });
    }

    private static byte ClampToByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)MathF.Round(value);
    }
}
