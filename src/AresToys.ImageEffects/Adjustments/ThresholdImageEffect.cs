using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Pixels above
/// the cutoff become white, below become black — produces a binary image. Useful for OCR
/// preprocessing and stylised silhouettes.</summary>
public sealed class ThresholdImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "threshold";
    public override string Name => "Threshold";

    [EffectParameter(0, 255, DisplayName = "Threshold")]
    public int Value { get; set; } = 128;

    public override SKBitmap Apply(SKBitmap source)
    {
        var threshold = Math.Clamp(Value, 0, 255);
        return ApplyPixelOperation(source, c =>
        {
            // BT.601 fixed-point luma — same shift used by ShareX. Cheaper than the float
            // BT.709 used elsewhere; precision doesn't matter once we threshold to 0/255.
            var luma = ((c.Red * 77) + (c.Green * 150) + (c.Blue * 29)) >> 8;
            var bw = (byte)(luma >= threshold ? 255 : 0);
            return new SKColor(bw, bw, bw, c.Alpha);
        });
    }
}
