using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Gaussian blur via <see cref="SKImageFilter.CreateBlur"/>. <see cref="Size"/> is
/// the blur sigma on each axis; small values (1-3) produce a slight softening, large values
/// (20+) heavy bokeh.</summary>
public sealed class BlurImageEffect : FilterImageEffectBase
{
    public override string Id => "blur";
    public override string Name => "Blur";

    [EffectParameter(0, 100, DisplayName = "Size")]
    public float Size { get; set; } = 3f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Size <= 0) return source.Copy();
        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(Size, Size) };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }
}
