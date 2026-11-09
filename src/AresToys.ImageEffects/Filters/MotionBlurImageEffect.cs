using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Approximated motion blur. Skia's stock blur is symmetric; we fake direction by
/// using a wider sigma on one axis. Placeholder — a true 1-D blur per angle wants a custom
/// shader / convolution kernel that we'll add in a later step.</summary>
public sealed class MotionBlurImageEffect : FilterImageEffectBase
{
    public override string Id => "motion_blur";
    public override string Name => "Motion blur";

    [EffectParameter(0, 100, DisplayName = "Distance")]
    public float Distance { get; set; } = 10f;

    [EffectParameter(0, 359, DisplayName = "Angle")]
    public float Angle { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Distance <= 0) return source.Copy();
        // Decompose distance into axis-aligned sigmas. Not a true motion blur (no streaking
        // along the angle), but produces a directional softening that's better than nothing.
        var rad = Angle * MathF.PI / 180f;
        var sx = Math.Abs(MathF.Cos(rad)) * Distance;
        var sy = Math.Abs(MathF.Sin(rad)) * Distance;
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(MathF.Max(sx, 0.1f), MathF.Max(sy, 0.1f)) };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }
}
