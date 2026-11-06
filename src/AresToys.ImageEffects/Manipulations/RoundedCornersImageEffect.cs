using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Effect inspired by ShareX.
/// Clips the bitmap to a rounded rectangle of the given corner radius. CornerRadius=0 is a
/// no-op that returns a defensive copy.</summary>
public sealed class RoundedCornersImageEffect : ManipulationImageEffectBase
{
    public override string Id => "rounded_corners";
    public override string Name => "Rounded Corners";

    /// <summary>Uniform radius applied to every corner. ShareX <c>.sxie</c> presets only
    /// have this single value, so importing them works out of the box. Set
    /// <see cref="UsePerCornerRadius"/> to override per side via the
    /// <see cref="TopLeftRadius"/> / … properties.</summary>
    [EffectParameter(0, 500, DisplayName = "Corner radius")]
    public int CornerRadius { get; set; } = 20;

    [EffectParameter(0, 500, DisplayName = "Top-left")]
    public int TopLeftRadius { get; set; }

    [EffectParameter(0, 500, DisplayName = "Top-right")]
    public int TopRightRadius { get; set; }

    [EffectParameter(0, 500, DisplayName = "Bottom-right")]
    public int BottomRightRadius { get; set; }

    [EffectParameter(0, 500, DisplayName = "Bottom-left")]
    public int BottomLeftRadius { get; set; }

    /// <summary>When true, the four per-corner radii are used instead of <see cref="CornerRadius"/>.
    /// Lets the user round only certain corners (e.g. only top for a "card with flat bottom"
    /// look). Default false → import-compatibility with single-radius .sxie presets.</summary>
    public bool UsePerCornerRadius { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Resolve effective per-corner radii. Without UsePerCornerRadius we fan the single
        // value out to all four corners (matches ShareX behaviour and means a fresh effect
        // shows the rounded look as soon as the user adds it).
        int tl, tr, br, bl;
        if (UsePerCornerRadius)
        {
            tl = Math.Max(0, TopLeftRadius);
            tr = Math.Max(0, TopRightRadius);
            br = Math.Max(0, BottomRightRadius);
            bl = Math.Max(0, BottomLeftRadius);
        }
        else
        {
            tl = tr = br = bl = Math.Max(0, CornerRadius);
        }
        if (tl == 0 && tr == 0 && br == 0 && bl == 0) return source.Copy();

        // Force a Premul alpha-typed bitmap regardless of the source flags. SrcIn blending
        // wants premultiplied destinations to compose correctly, and 8888-Bgra/Premul is
        // also what SKImage.Encode expects for clean PNG output.
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // SKRoundRect with per-corner radii lets us round only some sides. SetRectRadii takes
        // a SKPoint per corner (x = horizontal radius, y = vertical radius) — same value for
        // both gives a circular corner.
        using var roundRect = new SKRoundRect();
        roundRect.SetRectRadii(
            new SKRect(0, 0, source.Width, source.Height),
            new[] { new SKPoint(tl, tl), new SKPoint(tr, tr), new SKPoint(br, br), new SKPoint(bl, bl) });

        // Mask-based composition for clean sub-pixel AA at any radius:
        //  1) draw the rounded rect onto the canvas in white (antialias on)
        //  2) draw the bitmap with SrcIn — pixels survive only where the mask covered them
        using var maskPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawRoundRect(roundRect, maskPaint);

        using var bitmapPaint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcIn };
        canvas.DrawBitmap(source, 0, 0, bitmapPaint);

        return result;
    }
}
