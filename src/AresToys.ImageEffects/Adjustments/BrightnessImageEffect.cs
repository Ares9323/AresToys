using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.</summary>
public sealed class BrightnessImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "brightness";
    public override string Name => "Brightness";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var value = Amount / 100f;
        float[] matrix =
        {
            1, 0, 0, 0, value,
            0, 1, 0, 0, value,
            0, 0, 1, 0, value,
            0, 0, 0, 1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
