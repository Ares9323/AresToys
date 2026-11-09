using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Effect inspired by ShareX.
/// Custom-angle rotation. Orthogonal multiples of 90° take a fast path that swaps width/height
/// without subpixel sampling; arbitrary angles either expand the canvas (AutoResize) or clip
/// to the original bounds.</summary>
public sealed class RotateImageEffect : ManipulationImageEffectBase
{
    public override string Id => "rotate";
    public override string Name => "Rotate";

    [EffectParameter(-360, 360, 1, DisplayName = "Angle")]
    public float Angle { get; set; }

    public bool AutoResize { get; set; } = true;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Angle % 90 == 0 && AutoResize) return RotateOrthogonal(source, (int)Angle);
        return AutoResize ? RotateArbitrary(source, Angle) : RotateClipped(source, Angle);
    }

    private static SKBitmap RotateClipped(SKBitmap source, float angle)
    {
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(source.Width / 2f, source.Height / 2f);
        canvas.RotateDegrees(angle);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    private static SKBitmap RotateOrthogonal(SKBitmap source, int angle)
    {
        angle %= 360;
        if (angle < 0) angle += 360;
        int width = source.Width, height = source.Height;
        if (angle == 90 || angle == 270) (width, height) = (height, width);
        var result = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        switch (angle)
        {
            case 90: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
            case 180: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
            case 270: canvas.Translate(0, height); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    private static SKBitmap RotateArbitrary(SKBitmap source, float angle)
    {
        var matrix = SKMatrix.CreateRotationDegrees(angle, source.Width / 2f, source.Height / 2f);
        var rect = new SKRect(0, 0, source.Width, source.Height);
        var mapped = matrix.MapRect(rect);
        var newWidth = (int)Math.Ceiling(mapped.Width);
        var newHeight = (int)Math.Ceiling(mapped.Height);

        var result = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(newWidth / 2f, newHeight / 2f);
        canvas.RotateDegrees(angle);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
