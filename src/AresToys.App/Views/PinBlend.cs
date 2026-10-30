using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AresToys.App.Views;

/// <summary>How a pinned image mixes with what is behind it (issue #26). A window cannot blend with the
/// desktop, so the two keying modes turn the background colour into transparency instead: the result
/// over any backdrop is what multiply (white drops out) or screen (black drops out) would give.</summary>
public enum PinBlendMode
{
    Normal = 0,

    /// <summary>White becomes transparent and darker pixels keep their strength, like Multiply.</summary>
    Multiply = 1,

    /// <summary>Black becomes transparent and lighter pixels keep their strength, like Screen.</summary>
    Screen = 2,
}

internal static class PinBlend
{
    public static PinBlendMode Next(PinBlendMode mode)
        => (PinBlendMode)(((int)mode + 1) % Enum.GetValues<PinBlendMode>().Length);

    /// <summary>A persisted mode brought back into range (hand-edited or corrupt manifest).</summary>
    public static PinBlendMode Clamp(int value)
        => Enum.IsDefined(typeof(PinBlendMode), value) ? (PinBlendMode)value : PinBlendMode.Normal;

    /// <summary>The bitmap to show for <paramref name="mode"/>; <paramref name="source"/> itself for Normal.</summary>
    public static BitmapSource Apply(BitmapSource source, PinBlendMode mode)
    {
        if (mode == PinBlendMode.Normal) return source;

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        Transform(pixels, mode);

        var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Straight BGRA in, premultiplied BGRA out, in place.</summary>
    internal static void Transform(byte[] pixels, PinBlendMode mode)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            // Whatever was already transparent is flattened onto the keyed colour first.
            var keyed = mode == PinBlendMode.Multiply ? 255 : 0;
            var b = Flatten(pixels[i], alpha, keyed);
            var g = Flatten(pixels[i + 1], alpha, keyed);
            var r = Flatten(pixels[i + 2], alpha, keyed);

            if (mode == PinBlendMode.Multiply)
            {
                var min = Math.Min(b, Math.Min(g, r));
                pixels[i] = (byte)(b - min);
                pixels[i + 1] = (byte)(g - min);
                pixels[i + 2] = (byte)(r - min);
                pixels[i + 3] = (byte)(255 - min);
            }
            else
            {
                var max = Math.Max(b, Math.Max(g, r));
                pixels[i] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
                pixels[i + 3] = (byte)max;
            }
        }
    }

    private static int Flatten(int channel, int alpha, int backdrop)
        => (channel * alpha + backdrop * (255 - alpha) + 127) / 255;
}
