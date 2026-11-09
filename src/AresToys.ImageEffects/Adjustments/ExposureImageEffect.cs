using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Multiplies
/// every channel by 2^amount: +1 stop ≈ doubles brightness, −1 halves. Heavy clipping at the
/// extremes is expected (exposure is a destructive op on 8-bit RGB).</summary>
public sealed class ExposureImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "exposure";
    public override string Name => "Exposure";

    [EffectParameter(-10, 10, 0.1, DisplayName = "Stops", Decimals = 2)]
    public float Amount { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var amount = Math.Clamp(Amount, -10f, 10f);
        if (Math.Abs(amount) < 0.0001f) return source.Copy();

        var gain = MathF.Pow(2f, amount);
        return ApplyPixelOperation(source, c =>
            new SKColor(ClampToByte(c.Red * gain), ClampToByte(c.Green * gain), ClampToByte(c.Blue * gain), c.Alpha));
    }

    private static byte ClampToByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)MathF.Round(value);
    }
}
