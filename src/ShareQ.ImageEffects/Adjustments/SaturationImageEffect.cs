using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Adjustments;

/// <summary>Ported from ShareX (GPL v3) — Adjustments/SaturationImageEffect.cs.</summary>
public sealed class SaturationImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "saturation";
    public override string Name => "Saturation";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var x = 1f + (Amount / 100f);
        const float lumR = 0.3086f;
        const float lumG = 0.6094f;
        const float lumB = 0.0820f;

        var invSat = 1f - x;
        var r = invSat * lumR;
        var g = invSat * lumG;
        var b = invSat * lumB;

        float[] matrix =
        {
            r + x, g,     b,     0, 0,
            r,     g + x, b,     0, 0,
            r,     g,     b + x, 0, 0,
            0,     0,     0,     1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
