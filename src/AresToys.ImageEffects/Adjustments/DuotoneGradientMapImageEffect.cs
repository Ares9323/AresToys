using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Duotone: maps source luminance to a 2-stop gradient. <see cref="DarkColor"/>
/// represents pixels with luminance 0; <see cref="LightColor"/> luminance 1. Useful for the
/// retro magazine / poster look.</summary>
public sealed class DuotoneGradientMapImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "duotone_gradient_map";
    public override string Name => "Duotone";

    public SKColor DarkColor { get; set; } = new(20, 30, 80);
    public SKColor LightColor { get; set; } = new(245, 220, 180);

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var strength = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (strength <= 0) return source.Copy();
        return ApplyPixelOperation(source, c =>
        {
            var lum = ((0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue)) / 255f;
            var dr = DarkColor.Red + ((LightColor.Red - DarkColor.Red) * lum);
            var dg = DarkColor.Green + ((LightColor.Green - DarkColor.Green) * lum);
            var db = DarkColor.Blue + ((LightColor.Blue - DarkColor.Blue) * lum);
            var r = (byte)Math.Clamp(c.Red * (1 - strength) + dr * strength, 0, 255);
            var g = (byte)Math.Clamp(c.Green * (1 - strength) + dg * strength, 0, 255);
            var b = (byte)Math.Clamp(c.Blue * (1 - strength) + db * strength, 0, 255);
            return new SKColor(r, g, b, c.Alpha);
        });
    }
}
