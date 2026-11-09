using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Lomography colour grading — boosted saturation, lifted shadows, slight green
/// shift. Placeholder implementation pending the full curves/vignette pipeline ShareX uses.</summary>
public sealed class LomoImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "lomo";
    public override string Name => "Lomo";

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 60f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var s = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (s <= 0) return source.Copy();
        // Saturation +30%, slight green push, slight contrast lift.
        var sat = 1f + 0.3f * s;
        float[] matrix =
        {
            sat, 0,   0,   0, 0,
            0,   sat, 0,   0, 0.04f * s,
            0,   0,   sat, 0, -0.02f * s,
            0,   0,   0,   1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
