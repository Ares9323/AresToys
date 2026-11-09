using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Cross-process colour grading: skews curves to push midtones toward complementary
/// channels. Approximation of the legacy ShareX <c>CrossProcess</c> filter — placeholder
/// implementation that tilts shadows/highlights via a colour matrix until a proper LUT-based
/// version lands.</summary>
public sealed class CrossProcessImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "cross_process";
    public override string Name => "Cross process";

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 50f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var s = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (s <= 0) return source.Copy();
        // Skewed contrast + warm shadows + cool highlights. Approximate; not a full curve port.
        var bias = 0.1f * s;
        float[] matrix =
        {
            1.1f, 0,    0,   0, -bias,
            0,    1.0f, 0,   0, 0,
            0,    0,    0.9f, 0, bias,
            0,    0,    0,   1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
