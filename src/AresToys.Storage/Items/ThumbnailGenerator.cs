using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AresToys.Storage.Items;

/// <summary>Generates small PNG thumbnails from image payload bytes. Output: ≤ <paramref name="maxSide"/>
/// per side, aspect preserved, encoded as PNG (typical size 1-5 KB). Returns null on any failure
/// (corrupted bytes, unsupported format) — the caller stores null and the UI just shows a generic icon.</summary>
public static class ThumbnailGenerator
{
    public static byte[]? TryGenerate(ReadOnlyMemory<byte> source, int maxSide = 96)
    {
        if (source.IsEmpty) return null;
        try
        {
            using var ms = new MemoryStream(source.ToArray());
            using var img = Image.FromStream(ms);
            var (w, h) = ScaleToFit(img.Width, img.Height, maxSide);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(img, 0, 0, w, h);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch (ArgumentException) { return null; }
        catch (OutOfMemoryException) { return null; } // GDI+ throws this for malformed images
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private static (int W, int H) ScaleToFit(int origW, int origH, int maxSide)
    {
        if (origW <= maxSide && origH <= maxSide) return (Math.Max(1, origW), Math.Max(1, origH));
        var ratio = (double)origW / origH;
        return ratio >= 1
            ? (maxSide, Math.Max(1, (int)Math.Round(maxSide / ratio)))
            : (Math.Max(1, (int)Math.Round(maxSide * ratio)), maxSide);
    }
}
