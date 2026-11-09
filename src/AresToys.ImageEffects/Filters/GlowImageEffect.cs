using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Effect inspired by ShareX. Outer-glow
/// pass: blur the source's alpha mask, tint it with <see cref="Color"/>, draw it underneath
/// the original. Looks like a halo around the visible pixels — works on irregular alpha
/// masks (e.g. text, rounded corners) just like the analogous Shadow filter.</summary>
public sealed class GlowImageEffect : FilterImageEffectBase
{
    public override string Id => "glow";
    public override string Name => "Glow";

    [EffectParameter(1, 100, DisplayName = "Size")]
    public int Size { get; set; } = 20;

    [EffectParameter(1, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 80f;

    public SKColor Color { get; set; } = SKColors.White;

    [EffectParameter(-100, 100, DisplayName = "Offset X")]
    public int OffsetX { get; set; }

    [EffectParameter(-100, 100, DisplayName = "Offset Y")]
    public int OffsetY { get; set; }

    public bool AutoResize { get; set; } = true;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var pad = AutoResize ? Size : 0;
        var expandLeft = AutoResize ? Math.Max(0, -OffsetX) + pad : 0;
        var expandRight = AutoResize ? Math.Max(0, OffsetX) + pad : 0;
        var expandTop = AutoResize ? Math.Max(0, -OffsetY) + pad : 0;
        var expandBottom = AutoResize ? Math.Max(0, OffsetY) + pad : 0;

        var newWidth = source.Width + expandLeft + expandRight;
        var newHeight = source.Height + expandTop + expandBottom;
        var result = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        var imageX = expandLeft;
        var imageY = expandTop;
        var glowX = imageX + OffsetX;
        var glowY = imageY + OffsetY;
        // Tolerate both 0..1 (ShareX legacy) and 0..100 (modern) strength scales — same
        // dual-range trick used for ShadowImageEffect.Opacity.
        var strengthNorm = Strength > 1f ? Math.Clamp(Strength, 0f, 100f) / 100f : Math.Clamp(Strength, 0f, 1f);
        var glowColor = Color.WithAlpha((byte)(255 * strengthNorm));

        // Same SrcIn-blend trick as Shadow: keep only the source's alpha mask, recolour it,
        // blur it, then draw the original on top so the glow halo only shows on the outside.
        using var glowPaint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateBlendMode(glowColor, SKBlendMode.SrcIn),
            ImageFilter = SKImageFilter.CreateBlur(Size, Size),
        };
        canvas.DrawBitmap(source, glowX, glowY, glowPaint);
        canvas.DrawBitmap(source, imageX, imageY);
        return result;
    }
}
