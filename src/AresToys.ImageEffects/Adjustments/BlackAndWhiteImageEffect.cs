using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Hard
/// threshold (no mid-greys) — different from <see cref="GrayscaleImageEffect"/> which
/// produces continuous tones.</summary>
public sealed class BlackAndWhiteImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "black_and_white";
    public override string Name => "Black & White";

    public override SKBitmap Apply(SKBitmap source) =>
        ApplyPixelOperation(source, c =>
        {
            var lum = (0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue);
            return lum > 127 ? SKColors.White : SKColors.Black;
        });
}
