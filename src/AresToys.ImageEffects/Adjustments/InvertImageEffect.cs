using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.</summary>
public sealed class InvertImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "invert";
    public override string Name => "Invert";

    public override SKBitmap Apply(SKBitmap source)
    {
        float[] matrix =
        {
            -1, 0,  0,  0, 1,
            0,  -1, 0,  0, 1,
            0,  0,  -1, 0, 1,
            0,  0,  0,  1, 0,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
