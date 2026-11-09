using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Pixelate / mosaic. Downscales to <c>width / size</c> × <c>height / size</c>
/// using nearest-neighbour, then upscales back — produces square blocks of <see cref="Size"/>
/// pixels each.</summary>
public sealed class PixelateImageEffect : FilterImageEffectBase
{
    public override string Id => "pixelate";
    public override string Name => "Pixelate";

    [EffectParameter(2, 100, DisplayName = "Block size")]
    public int Size { get; set; } = 8;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Math.Max(2, Size);
        var smallW = Math.Max(1, source.Width / size);
        var smallH = Math.Max(1, source.Height / size);

        // Down-then-up with NearestNeighbour gives the classic mosaic look — Default sampling
        // would smooth the blocks out and defeat the effect.
        using var downsampled = source.Resize(new SKSizeI(smallW, smallH),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (downsampled is null) return source.Copy();

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        // DrawImage exposes an SKSamplingOptions overload (DrawBitmap doesn't in 3.116);
        // wrapping the downsampled bitmap as an SKImage gives us the nearest-neighbour
        // upscale we need to keep the pixelated edges crisp.
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var image = SKImage.FromBitmap(downsampled);
        canvas.DrawImage(image, new SKRect(0, 0, source.Width, source.Height), sampling);
        return result;
    }
}
