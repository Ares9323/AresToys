using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Adjustments;

/// <summary>Ported from ShareX (GPL v3) — Adjustments/ContrastImageEffect.cs.</summary>
public sealed class ContrastImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "contrast";
    public override string Name => "Contrast";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var scale = (100f + Amount) / 100f;
        scale *= scale;
        var shift = 0.5f * (1f - scale);

        float[] matrix =
        {
            scale, 0,     0,     0, shift,
            0,     scale, 0,     0, shift,
            0,     0,     scale, 0, shift,
            0,     0,     0,     1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
