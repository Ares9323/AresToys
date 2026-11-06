using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.</summary>
public sealed class PosterizeImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "posterize";
    public override string Name => "Posterize";

    [EffectParameter(2, 64, DisplayName = "Levels")]
    public int Levels { get; set; } = 8;

    public override SKBitmap Apply(SKBitmap source)
    {
        var levels = Math.Clamp(Levels, 2, 64);
        var scale = (float)(levels - 1);
        return ApplyPixelOperation(source, c =>
            new SKColor(Quantize(c.Red, scale), Quantize(c.Green, scale), Quantize(c.Blue, scale), c.Alpha));
    }

    private static byte Quantize(byte value, float scale)
    {
        var bucket = MathF.Round(value * scale / 255f);
        var mapped = bucket * 255f / scale;
        if (mapped <= 0f) return 0;
        if (mapped >= 255f) return 255;
        return (byte)MathF.Round(mapped);
    }
}
