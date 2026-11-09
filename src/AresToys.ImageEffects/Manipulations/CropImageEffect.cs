using AresToys.ImageEffects.Drawing;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Crop the image by removing pixels from each side per <see cref="Margin"/>.
/// Inverse of Canvas: positive values reduce the canvas, ignored when they'd produce a
/// non-positive size.</summary>
public sealed class CropImageEffect : ManipulationImageEffectBase
{
    public override string Id => "crop";
    public override string Name => "Crop";

    public Padding Margin { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var w = source.Width - Margin.Left - Margin.Right;
        var h = source.Height - Margin.Top - Margin.Bottom;
        if (w <= 0 || h <= 0) return source.Copy();
        var result = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, -Margin.Left, -Margin.Top);
        return result;
    }
}
