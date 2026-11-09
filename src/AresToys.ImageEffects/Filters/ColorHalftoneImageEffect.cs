using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Newsprint-style halftone. Placeholder implementation: pixelates aggressively
/// then hard-thresholds each cell — produces a "comic book dot" feel without the proper
/// per-channel angle separation real halftone uses.</summary>
public sealed class ColorHalftoneImageEffect : FilterImageEffectBase
{
    public override string Id => "color_halftone";
    public override string Name => "Color halftone";

    [EffectParameter(2, 50, DisplayName = "Dot size")]
    public int DotSize { get; set; } = 6;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Math.Max(2, DotSize);

        // Approximate halftone via downscale-NN then threshold the pixel block alpha.
        var smallW = Math.Max(1, source.Width / size);
        var smallH = Math.Max(1, source.Height / size);
        using var down = source.Resize(new SKSizeI(smallW, smallH),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (down is null) return source.Copy();

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        for (var y = 0; y < smallH; y++)
        {
            for (var x = 0; x < smallW; x++)
            {
                var c = down.GetPixel(x, y);
                var lum = ((0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue)) / 255f;
                var radius = (1f - lum) * size * 0.5f;
                if (radius < 0.5f) continue;
                using var paint = new SKPaint { IsAntialias = true, Color = c };
                canvas.DrawCircle((x * size) + (size / 2f), (y * size) + (size / 2f), radius, paint);
            }
        }
        return result;
    }
}
