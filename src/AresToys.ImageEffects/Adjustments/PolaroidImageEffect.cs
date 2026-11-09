using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Polaroid colour grading — warm tone, slight desaturation, lifted blacks.
/// Approximation pending a full LUT-based port.</summary>
public sealed class PolaroidImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "polaroid";
    public override string Name => "Polaroid";

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 70f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var s = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (s <= 0) return source.Copy();
        // +R, +G slight, -B; lifted shadows; reduced contrast.
        var lift = 0.05f * s;
        float[] matrix =
        {
            1f + 0.1f * s, 0,             0,             0, lift,
            0,             1f + 0.05f * s, 0,             0, lift,
            0,             0,              1f - 0.1f * s, 0, lift * 0.5f,
            0,             0,              0,             1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
