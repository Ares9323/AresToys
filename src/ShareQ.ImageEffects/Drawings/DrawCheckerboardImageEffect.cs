using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Drawings;

/// <summary>Ported from ShareX (GPL v3) — ImageEffectsLib/Drawings/DrawCheckerboard.cs. Fills
/// the source bitmap with a two-colour checker pattern (sized in pixels per square).
/// Used as a transparency-style backdrop in presets like Windows98 / ShareXBorderRounded2.</summary>
public sealed class DrawCheckerboardImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_checkerboard";
    public override string Name => "Checkerboard";

    [EffectParameter(1, 100, DisplayName = "Size")]
    public int Size { get; set; } = 10;

    public SKColor Color { get; set; } = new(0xD3, 0xD3, 0xD3);
    public SKColor Color2 { get; set; } = SKColors.White;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Math.Max(1, Size);

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);

        // Two flat fills + DrawRect for the alternate cells. We could build a tiled shader
        // (faster on huge canvases), but a 10px checker on a 1080p image is ~20k rects which
        // Skia handles in under a millisecond — keeping it simple here.
        canvas.Clear(Color);
        using var paint = new SKPaint { Color = Color2, IsAntialias = false };
        for (var y = 0; y < source.Height; y += size)
        {
            for (var x = 0; x < source.Width; x += size)
            {
                if (((x / size) + (y / size)) % 2 == 0) continue;
                canvas.DrawRect(x, y, size, size, paint);
            }
        }
        return result;
    }
}
