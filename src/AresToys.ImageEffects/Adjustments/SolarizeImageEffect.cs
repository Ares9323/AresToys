using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Inverts every
/// channel value above the threshold; channel values below are left unchanged. Produces the
/// classic darkroom solarisation look.</summary>
public sealed class SolarizeImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "solarize";
    public override string Name => "Solarize";

    [EffectParameter(0, 255, DisplayName = "Threshold")]
    public int Threshold { get; set; } = 128;

    public override SKBitmap Apply(SKBitmap source)
    {
        var t = Math.Clamp(Threshold, 0, 255);
        return ApplyPixelOperation(source, c =>
        {
            var r = c.Red > t ? (byte)(255 - c.Red) : c.Red;
            var g = c.Green > t ? (byte)(255 - c.Green) : c.Green;
            var b = c.Blue > t ? (byte)(255 - c.Blue) : c.Blue;
            return new SKColor(r, g, b, c.Alpha);
        });
    }
}
