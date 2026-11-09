using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Manipulations;

/// <summary>Resize the image to <see cref="Width"/> × <see cref="Height"/>. Either dimension
/// at 0 keeps the original on that axis (useful for "fit to width, keep aspect").</summary>
public sealed class ResizeImageEffect : ManipulationImageEffectBase
{
    public override string Id => "resize";
    public override string Name => "Resize";

    [EffectParameter(0, 8000, DisplayName = "Width (0 = auto)")]
    public int Width { get; set; }

    [EffectParameter(0, 8000, DisplayName = "Height (0 = auto)")]
    public int Height { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Width <= 0 && Height <= 0) return source.Copy();
        var w = Width > 0 ? Width : source.Width * (Height / (float)source.Height);
        var h = Height > 0 ? Height : source.Height * (Width / (float)source.Width);
        return source.Resize(new SKSizeI((int)w, (int)h), SKSamplingOptions.Default) ?? source.Copy();
    }
}
