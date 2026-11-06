using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.</summary>
public sealed class SepiaImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "sepia";
    public override string Name => "Sepia";

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var s = Math.Clamp(Strength / 100f, 0f, 1f);
        if (s <= 0) return source.Copy();

        // Blend identity → sepia matrix by strength so 50% = "half-sepia, half-original".
        float[] sepia =
        {
            0.393f, 0.769f, 0.189f, 0, 0,
            0.349f, 0.686f, 0.168f, 0, 0,
            0.272f, 0.534f, 0.131f, 0, 0,
            0,      0,      0,      1, 0,
        };
        float[] identity =
        {
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
        };
        var matrix = new float[20];
        for (var i = 0; i < 20; i++) matrix[i] = identity[i] * (1 - s) + sepia[i] * s;
        return ApplyColorMatrix(source, matrix);
    }
}
