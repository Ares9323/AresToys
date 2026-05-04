using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Manipulations;

/// <summary>Ported from ShareX (GPL v3) — ImageEffectsLib/Manipulations/AutoCrop.cs. Detects
/// the bounding box of non-transparent pixels and crops the bitmap to it, with optional
/// padding around the result. Used by templates like GoldBorder where the user wants the
/// transparent margin trimmed before further effects fire.</summary>
public sealed class AutoCropImageEffect : ManipulationImageEffectBase
{
    public override string Id => "auto_crop";
    public override string Name => "Auto crop";

    [EffectParameter(0, 200, DisplayName = "Padding")]
    public int Padding { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bounds = MeasureContentBounds(source);
        if (bounds is null) return source.Copy();

        var rect = bounds.Value;
        var pad = Math.Max(0, Padding);
        var newWidth = rect.Width + (pad * 2);
        var newHeight = rect.Height + (pad * 2);
        if (newWidth <= 0 || newHeight <= 0) return source.Copy();

        var result = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        var srcRect = SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height);
        var destRect = SKRect.Create(pad, pad, rect.Width, rect.Height);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, srcRect, destRect);
        return result;
    }

    /// <summary>Walk the alpha channel from each side until we find a non-transparent pixel.
    /// Returns null when the whole bitmap is transparent (caller no-ops the crop). We read
    /// pixels via <see cref="SKBitmap.GetPixels"/> for the byte-level scan rather than the
    /// per-pixel <see cref="SKBitmap.GetPixel"/> overload — that one boxes through SKColor
    /// per call which is far too slow for full-image scans.</summary>
    private static SKRectI? MeasureContentBounds(SKBitmap source)
    {
        // Only the BGRA layout is portable enough to scan in-place; for anything else we'd
        // need to copy into a known-format pixmap. ShareX's source is always RGBA / BGRA so
        // this branch handles the realistic cases.
        if (source.ColorType != SKColorType.Bgra8888 && source.ColorType != SKColorType.Rgba8888)
            return new SKRectI(0, 0, source.Width, source.Height);

        var width = source.Width;
        var height = source.Height;
        var rowBytes = source.RowBytes;
        unsafe
        {
            var pixels = (byte*)source.GetPixels().ToPointer();
            // BGRA / RGBA both put alpha at byte 3 per pixel.
            const int alphaOffset = 3;

            int top = -1, bottom = -1, left = -1, right = -1;
            for (var y = 0; y < height; y++)
            {
                var row = pixels + (y * rowBytes);
                for (var x = 0; x < width; x++)
                {
                    if (row[(x * 4) + alphaOffset] == 0) continue;
                    if (top < 0) top = y;
                    bottom = y;
                    if (left < 0 || x < left) left = x;
                    if (right < 0 || x > right) right = x;
                }
            }

            if (top < 0) return null;
            return new SKRectI(left, top, right + 1, bottom + 1);
        }
    }
}
