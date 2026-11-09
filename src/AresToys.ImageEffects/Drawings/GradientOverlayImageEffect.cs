using AresToys.ImageEffects.Drawing;
using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Drawings;

/// <summary>Linear gradient blended on top of the source. Useful for fading an image to a
/// solid colour or layering a colour wash over photos.</summary>
public sealed class GradientOverlayImageEffect : DrawingImageEffectBase
{
    public override string Id => "gradient_overlay";
    public override string Name => "Gradient overlay";

    public GradientInfo Gradient { get; set; } = new(LinearGradientMode.Vertical,
        new GradientStop(new SKColor(0, 0, 0, 0), 0f),
        new GradientStop(new SKColor(0, 0, 0, 180), 100f));

    [EffectParameter(0, 100, DisplayName = "Opacity")]
    public float Opacity { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Gradient.IsValid || Opacity <= 0) return source.Copy();

        var result = source.Copy();
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = Gradient.CreateShader(source.Width, source.Height),
            Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(Opacity, 0f, 100f) / 100f)),
        };
        canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        return result;
    }
}
