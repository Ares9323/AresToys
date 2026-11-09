using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Rotate 90° clockwise — discrete fast-path version (no antialiasing pass needed).</summary>
public sealed class Rotate90CWImageEffect : ManipulationImageEffectBase
{
    public override string Id => "rotate_90_cw";
    public override string Name => "Rotate 90° CW";
    public override SKBitmap Apply(SKBitmap source) => RotateOrthogonal(source, 90);

    internal static SKBitmap RotateOrthogonal(SKBitmap source, int angle)
    {
        ArgumentNullException.ThrowIfNull(source);
        angle = ((angle % 360) + 360) % 360;
        int w = source.Width, h = source.Height;
        if (angle == 90 || angle == 270) (w, h) = (h, w);
        var result = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        switch (angle)
        {
            case 90: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case 180: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case 270: canvas.Translate(0, h); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}

/// <summary>Rotate 90° counter-clockwise.</summary>
public sealed class Rotate90CCWImageEffect : ManipulationImageEffectBase
{
    public override string Id => "rotate_90_ccw";
    public override string Name => "Rotate 90° CCW";
    public override SKBitmap Apply(SKBitmap source) => Rotate90CWImageEffect.RotateOrthogonal(source, 270);
}

/// <summary>Rotate 180°.</summary>
public sealed class Rotate180ImageEffect : ManipulationImageEffectBase
{
    public override string Id => "rotate_180";
    public override string Name => "Rotate 180°";
    public override SKBitmap Apply(SKBitmap source) => Rotate90CWImageEffect.RotateOrthogonal(source, 180);
}
