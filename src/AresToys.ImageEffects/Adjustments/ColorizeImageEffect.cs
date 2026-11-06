using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Desaturates
/// the source then tints toward <see cref="Color"/>; <see cref="Strength"/> blends with the
/// original.</summary>
public sealed class ColorizeImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "colorize";
    public override string Name => "Colorize";

    public SKColor Color { get; set; } = SKColors.Red;

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 50f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var strength = Math.Clamp(Strength, 0f, 100f);
        if (strength <= 0) return source.Copy();

        // Desaturate → Modulate-blend with target colour. CreateCompose chains them so we get
        // a single colour-filter pass instead of two bitmap copies.
        float[] grayscale =
        {
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0,       0,       0,       1, 0,
        };
        using var gray = SKColorFilter.CreateColorMatrix(grayscale);
        using var tint = SKColorFilter.CreateBlendMode(Color, SKBlendMode.Modulate);
        using var composed = SKColorFilter.CreateCompose(tint, gray);
        using var paint = new SKPaint { ColorFilter = composed };

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        if (strength >= 100)
        {
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        else
        {
            // Partial blend: original underneath, colorized layer on top with reduced alpha.
            canvas.DrawBitmap(source, 0, 0);
            paint.Color = new SKColor(255, 255, 255, (byte)(255 * (strength / 100f)));
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        return result;
    }
}
