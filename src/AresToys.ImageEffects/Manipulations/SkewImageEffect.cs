using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Skew (shear) the image horizontally / vertically. Values in pixels — represents
/// the offset applied to the opposite edge. The canvas grows to keep the full sheared image
/// visible.</summary>
public sealed class SkewImageEffect : ManipulationImageEffectBase
{
    public override string Id => "skew";
    public override string Name => "Skew";

    [EffectParameter(-500, 500, DisplayName = "Horizontal")]
    public int Horizontal { get; set; }

    [EffectParameter(-500, 500, DisplayName = "Vertical")]
    public int Vertical { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Horizontal == 0 && Vertical == 0) return source.Copy();

        var hSkew = Horizontal / (float)source.Height;
        var vSkew = Vertical / (float)source.Width;
        var newW = source.Width + Math.Abs(Horizontal);
        var newH = source.Height + Math.Abs(Vertical);

        var result = new SKBitmap(newW, newH, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(Math.Max(0, -Horizontal), Math.Max(0, -Vertical));
        canvas.Skew(hSkew, vSkew);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
