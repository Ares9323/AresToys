using System.IO;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AresToys.App.Services.ImageFiles;

/// <summary>Turns encoded image bytes into a frozen <see cref="BitmapSource"/> that can cross
/// threads and bind straight into XAML. WebP goes through Skia: the WIC WebP codec (Windows
/// "WebP Image Extension") decodes to Bgr32 and drops the alpha channel, so a transparent WebP
/// would render on black. Everything else uses WIC. With <c>maxSide</c> set, the image is decoded
/// directly at that size (WIC DecodePixelWidth/Height, Skia scaled decode), so a thumbnail of a
/// huge photo never materialises the full resolution bitmap.</summary>
public static class ImageDecoding
{
    /// <summary>RIFF container with a WEBP form type.</summary>
    public static bool IsWebP(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12
           && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
           && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    /// <summary>Decode <paramref name="bytes"/>; null when they aren't a decodable image.
    /// <paramref name="maxSide"/> &gt; 0 caps the longer side (aspect preserved, never upscaled).</summary>
    public static BitmapSource? Decode(byte[] bytes, int maxSide = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            if (IsWebP(bytes))
            {
                var webp = DecodeWithSkia(bytes, maxSide);
                if (webp is not null) return webp;
                // Skia refused it: let WIC try, an opaque result beats no image at all.
            }
            return DecodeWithWic(bytes, maxSide);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException
                                       or InvalidOperationException or OverflowException or FormatException
                                       or System.Runtime.InteropServices.ExternalException
                                       or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Pixel size read from the image header only (no pixel decode); null when the
    /// bytes aren't a readable image.</summary>
    public static (int Width, int Height)? TryGetPixelSize(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            if (IsWebP(bytes))
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec is not null) return (codec.Info.Width, codec.Info.Height);
            }
            using var ms = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException
                                       or InvalidOperationException or OverflowException or FormatException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>Longer side capped at <paramref name="maxSide"/>, aspect preserved, never
    /// upscaled, never below 1 px. <paramref name="maxSide"/> &lt;= 0 keeps the size.</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0) return (Math.Max(1, width), Math.Max(1, height));
        if (maxSide <= 0 || (width <= maxSide && height <= maxSide)) return (width, height);
        return width >= height
            ? (maxSide, Math.Max(1, (int)Math.Round((double)height * maxSide / width)))
            : (Math.Max(1, (int)Math.Round((double)width * maxSide / height)), maxSide);
    }

    private static BitmapSource DecodeWithWic(byte[] bytes, int maxSide)
    {
        int decodeWidth = 0, decodeHeight = 0;
        if (maxSide > 0)
        {
            // Header only read: DelayCreation + CacheOption.None leave the pixels untouched.
            using var probe = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth > maxSide || frame.PixelHeight > maxSide)
            {
                if (frame.PixelWidth >= frame.PixelHeight) decodeWidth = maxSide;
                else decodeHeight = maxSide;
            }
        }

        using var ms = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource? DecodeWithSkia(byte[] bytes, int maxSide)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var source = codec.Info;
        var (w, h) = FitWithin(source.Width, source.Height, maxSide);

        SKBitmap? bitmap = null;
        if (w != source.Width || h != source.Height)
        {
            // libwebp scales while decoding, so the full size bitmap never exists.
            var scaled = codec.GetScaledDimensions((float)w / source.Width);
            bitmap = SKBitmap.Decode(codec, new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        bitmap ??= SKBitmap.Decode(codec, new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (bitmap is null) return null;

        using (bitmap)
        {
            if (bitmap.Width > w || bitmap.Height > h)
            {
                using var resized = bitmap.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                return resized is null ? null : ImageEffects.SkiaToWpfBitmap.Convert(resized);
            }
            return ImageEffects.SkiaToWpfBitmap.Convert(bitmap);
        }
    }

    /// <summary>PNG encode of a decoded bitmap (thumbnails stored in the items table).</summary>
    public static byte[] EncodePng(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
