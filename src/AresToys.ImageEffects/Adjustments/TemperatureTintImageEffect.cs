using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX.
/// Temperature shifts blue ↔ red (cool/warm); tint shifts magenta ↔ green.</summary>
public sealed class TemperatureTintImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "temperature_tint";
    public override string Name => "Temperature / Tint";

    [EffectParameter(-100, 100, DisplayName = "Temperature")]
    public float Temperature { get; set; }

    [EffectParameter(-100, 100, DisplayName = "Tint")]
    public float Tint { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        var temperature = Math.Clamp(Temperature, -100f, 100f);
        var tint = Math.Clamp(Tint, -100f, 100f);
        if (Math.Abs(temperature) < 0.0001f && Math.Abs(tint) < 0.0001f) return source.Copy();

        var tempDelta = temperature / 100f * 64f;
        var tintDelta = tint / 100f * 64f;

        return ApplyPixelOperation(source, c =>
        {
            var r = c.Red + tempDelta - tintDelta * 0.25f;
            var g = c.Green + tintDelta;
            var b = c.Blue - tempDelta - tintDelta * 0.25f;
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
