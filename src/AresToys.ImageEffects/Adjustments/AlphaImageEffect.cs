using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.</summary>
public sealed class AlphaImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "alpha";
    public override string Name => "Alpha";

    [EffectParameter(0, 100, DisplayName = "Alpha")]
    public float Amount { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var a = Amount / 100f;
        float[] matrix =
        {
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, a, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
