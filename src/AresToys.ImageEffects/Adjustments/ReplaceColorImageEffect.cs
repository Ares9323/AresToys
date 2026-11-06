using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Replaces
/// pixels matching <see cref="TargetColor"/> within <see cref="Tolerance"/> by
/// <see cref="ReplaceColor"/>.</summary>
public sealed class ReplaceColorImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "replace_color";
    public override string Name => "Replace Color";

    public SKColor TargetColor { get; set; } = SKColors.White;
    public SKColor ReplaceColor { get; set; } = SKColors.Black;

    [EffectParameter(0, 255, DisplayName = "Tolerance")]
    public float Tolerance { get; set; } = 40f;

    public override SKBitmap Apply(SKBitmap source)
    {
        // Tolerance is 0..255 per channel; the input slider runs 0..100 in ShareX (×2.55).
        // We treat the property value as the channel-space tolerance directly.
        var tol = (int)Tolerance;
        return ApplyPixelOperation(source, c =>
            Math.Abs(c.Red - TargetColor.Red) <= tol
            && Math.Abs(c.Green - TargetColor.Green) <= tol
            && Math.Abs(c.Blue - TargetColor.Blue) <= tol
                ? ReplaceColor
                : c);
    }
}
