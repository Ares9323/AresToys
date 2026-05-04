using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Adjustments;

/// <summary>Ported from ShareX (GPL v3) — Adjustments/GrayscaleImageEffect.cs.</summary>
public sealed class GrayscaleImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "grayscale";
    public override string Name => "Grayscale";

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var strength = Strength;
        if (strength >= 100f)
        {
            float[] matrix =
            {
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0,       0,       0,       1, 0,
            };
            return ApplyColorMatrix(source, matrix);
        }

        if (strength <= 0f) return source.Copy();

        var s = strength / 100f;
        var invS = 1f - s;
        float[] blended =
        {
            0.2126f * s + invS, 0.7152f * s,        0.0722f * s,        0, 0,
            0.2126f * s,        0.7152f * s + invS, 0.0722f * s,        0, 0,
            0.2126f * s,        0.7152f * s,        0.0722f * s + invS, 0, 0,
            0,                  0,                  0,                  1, 0,
        };
        return ApplyColorMatrix(source, blended);
    }
}
