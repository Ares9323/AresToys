using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Effect inspired by ShareX. Drops a
/// blurred copy of the alpha mask underneath the source bitmap, tinted with
/// <see cref="Color"/> and faded by <see cref="Opacity"/>. <see cref="AutoResize"/> grows
/// the canvas so the shadow isn't clipped against the original bounds — important for the
/// .sxie templates that chain Shadow → Canvas to produce floating-card looks.</summary>
public sealed class ShadowImageEffect : ManipulationImageEffectBase
{
    public override string Id => "shadow";
    public override string Name => "Shadow";

    [EffectParameter(0, 100, DisplayName = "Opacity")]
    public float Opacity { get; set; } = 80f;

    [EffectParameter(0, 100, DisplayName = "Size")]
    public int Size { get; set; } = 20;

    /// <summary>Additional darkening of <see cref="Color"/>: 0 = use Color as-is, 1 = full
    /// black regardless of Color. Stored 0..1 in ShareX legacy presets.</summary>
    [EffectParameter(0, 1, 0.05, DisplayName = "Darkness", Decimals = 2)]
    public float Darkness { get; set; }

    public SKColor Color { get; set; } = SKColors.Black;

    [EffectParameter(-1000, 1000, DisplayName = "Offset X")]
    public int OffsetX { get; set; } = 5;

    [EffectParameter(-1000, 1000, DisplayName = "Offset Y")]
    public int OffsetY { get; set; } = 5;

    public bool AutoResize { get; set; } = true;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var blurPad = Size;
        var expandLeft = AutoResize ? Math.Max(0, -OffsetX) + blurPad : 0;
        var expandRight = AutoResize ? Math.Max(0, OffsetX) + blurPad : 0;
        var expandTop = AutoResize ? Math.Max(0, -OffsetY) + blurPad : 0;
        var expandBottom = AutoResize ? Math.Max(0, OffsetY) + blurPad : 0;

        var newWidth = source.Width + expandLeft + expandRight;
        var newHeight = source.Height + expandTop + expandBottom;
        var result = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        var imageX = expandLeft;
        var imageY = expandTop;
        var shadowX = imageX + OffsetX;
        var shadowY = imageY + OffsetY;

        // Tolerate both opacity scales:
        //   - ShareX legacy (.sxie pre-Avalonia)  → 0..1
        //   - ShareX modern + our default UI     → 0..100
        // A value > 1 is unambiguously the latter; <= 1 is treated as the former.
        var opacityNorm = Opacity > 1f ? Math.Clamp(Opacity, 0f, 100f) / 100f : Math.Clamp(Opacity, 0f, 1f);
        // Darkness blends Color toward black before the alpha is applied.
        var darkness = Math.Clamp(Darkness, 0f, 1f);
        var shadowColor = new SKColor(
            (byte)(Color.Red * (1 - darkness)),
            (byte)(Color.Green * (1 - darkness)),
            (byte)(Color.Blue * (1 - darkness)),
            (byte)(255 * opacityNorm));

        // SrcIn blend → keep only the pixels covered by the source's alpha mask, paint them
        // shadowColor; the blur filter fluffs the result. Equivalent to CSS box-shadow on
        // an irregular alpha-mask shape.
        using var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateBlendMode(shadowColor, SKBlendMode.SrcIn),
            ImageFilter = SKImageFilter.CreateBlur(Size / 2f, Size / 2f),
        };
        canvas.DrawBitmap(source, shadowX, shadowY, paint);
        canvas.DrawBitmap(source, imageX, imageY);
        return result;
    }
}
