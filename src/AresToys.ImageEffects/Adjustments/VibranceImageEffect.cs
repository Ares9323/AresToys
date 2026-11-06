using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Boosts low-
/// saturation pixels more than already-saturated ones (so blue skies pop without making
/// already-vivid reds explode). Different from plain Saturation, which scales every pixel
/// uniformly.</summary>
public sealed class VibranceImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "vibrance";
    public override string Name => "Vibrance";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; } = 25f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var amount = Math.Clamp(Amount, -100f, 100f) / 100f;
        if (Math.Abs(amount) < 0.0001f) return source.Copy();

        return ApplyPixelOperation(source, c =>
        {
            float r = c.Red, g = c.Green, b = c.Blue;
            var max = MathF.Max(r, MathF.Max(g, b));
            var min = MathF.Min(r, MathF.Min(g, b));
            var saturation = (max - min) / 255f;
            var gray = (r + g + b) / 3f;

            // Positive amount: scale by (1 + amount * (1 - saturation)) → boost grayer pixels
            // more. Negative amount: uniform desaturate by (1 + amount).
            var factor = amount >= 0f ? 1f + (amount * (1f - saturation)) : 1f + amount;
            r = gray + ((r - gray) * factor);
            g = gray + ((g - gray) * factor);
            b = gray + ((b - gray) * factor);
            return new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), c.Alpha);
        });
    }

    private static byte ClampToByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)MathF.Round(value);
    }
}
