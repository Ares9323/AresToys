using AresToys.ImageEffects.Drawing;
using SkiaSharp;

namespace AresToys.ImageEffects.Drawings;

/// <summary>Effect inspired by ShareX. Fills
/// the canvas with a solid <see cref="Color"/> or, when <see cref="UseGradient"/> is set,
/// with the linear <see cref="Gradient"/>, then paints the source image on top — typically
/// preceded by a <c>Canvas</c> step that has expanded the canvas with transparent margins so
/// the new background actually shows around the picture.</summary>
public sealed class DrawBackgroundImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_background";
    public override string Name => "Background";

    public SKColor Color { get; set; } = SKColors.Black;
    public bool UseGradient { get; set; }
    public GradientInfo Gradient { get; set; } = DefaultGradient();

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);

        if (UseGradient && Gradient.IsValid)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = Gradient.CreateShader(source.Width, source.Height),
            };
            canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        }
        else
        {
            using var paint = new SKPaint { IsAntialias = true, Color = Color };
            canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        }

        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// <summary>Same default ShareX uses for fresh DrawBackground instances — a deep blue
    /// 4-stop gradient. Helps the user see *something* on first add instead of a default-
    /// black opaque rectangle that hides the rest of the chain.</summary>
    private static GradientInfo DefaultGradient() => new(
        LinearGradientMode.Vertical,
        new GradientStop(new SKColor(68, 120, 194), 0f),
        new GradientStop(new SKColor(13, 58, 122), 50f),
        new GradientStop(new SKColor(6, 36, 78), 50f),
        new GradientStop(new SKColor(23, 89, 174), 100f));
}
