using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AresToys.App.Services.ImageResize;

/// <summary>Resize and re-encode engine shared by the resize pipeline tasks.
/// Encoder stack: SkiaSharp decodes (PNG, JPEG, WebP, BMP, GIF and more), resizes and encodes
/// PNG, JPEG and WebP. WPF encodes BMP, TIFF and GIF (Skia has no encoder for them) and is the
/// decode fallback for formats Skia cannot read (TIFF). Pixels travel as premultiplied BGRA.</summary>
public static class ImageResizeEngine
{
    private const double DefaultDpi = 96.0;

    private readonly record struct Plan(int ScaledWidth, int ScaledHeight, int CropWidth, int CropHeight, bool NoOp);

    /// <summary>Output size for a source of <paramref name="srcW"/> x <paramref name="srcH"/>.
    /// Order follows PowerToys ImageResizer: unit to pixels, orientation swap, Fit/Fill/Stretch
    /// scale, shrink-only short circuit (skipped for Percent), rounding with minimum 1.
    /// For Fill that needs a crop, the returned size is the crop target.</summary>
    public static (int Width, int Height) CalculateDimensions(int srcW, int srcH, double dpiX, double dpiY, ImageResizeOptions o)
    {
        var p = Plan_(srcW, srcH, dpiX, dpiY, o);
        return p.CropWidth > 0 ? (p.CropWidth, p.CropHeight) : (p.ScaledWidth, p.ScaledHeight);
    }

    private static Plan Plan_(int srcW, int srcH, double dpiX, double dpiY, ImageResizeOptions o)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);
        if (!(dpiX > 0)) dpiX = DefaultDpi;
        if (!(dpiY > 0)) dpiY = DefaultDpi;

        var width = ToPixels(o.Width, o.Unit, o.Fit, srcW, dpiX);
        var height = ToPixels(o.Unit == ResizeUnit.Percent && o.Fit != ResizeFit.Stretch ? o.Width : o.Height,
            o.Unit, o.Fit, srcH, dpiY);

        var hasAuto = o.Width <= 0 || o.Height <= 0;
        if (o.IgnoreOrientation && !hasAuto && o.Unit != ResizeUnit.Percent)
        {
            var inLandscape = srcW > srcH;
            var inPortrait = srcH > srcW;
            var targetLandscape = width > height;
            var targetPortrait = height > width;
            if ((inLandscape && targetPortrait) || (inPortrait && targetLandscape))
                (width, height) = (height, width);
        }

        var scaleX = width / srcW;
        var scaleY = height / srcH;

        if (o.Fit == ResizeFit.Fit)
        {
            scaleX = Math.Min(scaleX, scaleY);
            scaleY = scaleX;
            if (!double.IsFinite(scaleX)) { scaleX = 1; scaleY = 1; }
        }
        else if (o.Fit == ResizeFit.Fill)
        {
            scaleX = Math.Max(scaleX, scaleY);
            scaleY = scaleX;
        }

        if (o.ShrinkOnly && o.Unit != ResizeUnit.Percent)
        {
            if (scaleX > 1 || scaleY > 1) return new Plan(srcW, srcH, 0, 0, true);
            var cropRequired = o.Fit == ResizeFit.Fill && (srcW > width || srcH > height);
            if (scaleX == 1 && scaleY == 1 && !cropRequired) return new Plan(srcW, srcH, 0, 0, true);
        }

        var scaledW = Math.Max(1, (int)Math.Round(srcW * scaleX));
        var scaledH = Math.Max(1, (int)Math.Round(srcH * scaleY));

        if (o.Fit == ResizeFit.Fill)
        {
            var cropW = Math.Max(1, (int)width);
            var cropH = Math.Max(1, (int)height);
            if (scaledW > cropW || scaledH > cropH)
                return new Plan(scaledW, scaledH, Math.Min(cropW, scaledW), Math.Min(cropH, scaledH), false);
        }

        return new Plan(scaledW, scaledH, 0, 0, false);
    }

    private static double ToPixels(double value, ResizeUnit unit, ResizeFit fit, int original, double dpi)
    {
        if (value <= 0 || double.IsNaN(value))
            return fit == ResizeFit.Fit ? double.PositiveInfinity : original;
        return unit switch
        {
            ResizeUnit.Inch => value * dpi,
            ResizeUnit.Centimeter => value * dpi / 2.54,
            ResizeUnit.Percent => value / 100.0 * original,
            _ => value,
        };
    }

    /// <summary>Decodes <paramref name="input"/>, optionally resizes it, and encodes it in the
    /// format chosen by <paramref name="encode"/>. <paramref name="extension"/> is the output
    /// extension without dot (jpg for Jpeg). EXIF orientation is always applied to the pixels.
    /// Metadata is not carried over by any of the encoders, so StripMetadata is effectively
    /// always honored. Throws <see cref="InvalidDataException"/> when the input cannot be decoded.</summary>
    public static byte[] Process(byte[] input, ImageResizeOptions? resize, ImageEncodeOptions encode, out string extension)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(encode);

        using var decoded = Decode(input, out var sourceFormat, out var dpiX, out var dpiY);
        var target = encode.Format == ImageOutputFormat.Keep ? sourceFormat : encode.Format;

        SKBitmap? resized = null;
        try
        {
            var work = decoded;
            if (resize is not null)
            {
                var plan = Plan_(decoded.Width, decoded.Height, dpiX, dpiY, resize);
                if (!plan.NoOp)
                {
                    resized = Resize(decoded, plan);
                    work = resized;
                }
            }
            return Encode(work, target, encode.Quality, out extension);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static SKBitmap Resize(SKBitmap src, Plan plan)
    {
        SKBitmap scaled = src;
        var ownsScaled = false;
        if (plan.ScaledWidth != src.Width || plan.ScaledHeight != src.Height)
        {
            var info = new SKImageInfo(plan.ScaledWidth, plan.ScaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            var upscale = plan.ScaledWidth > src.Width || plan.ScaledHeight > src.Height;
            var sampling = upscale
                ? new SKSamplingOptions(SKCubicResampler.Mitchell)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            scaled = src.Resize(info, sampling)
                ?? throw new InvalidOperationException("SkiaSharp failed to resize the image.");
            ownsScaled = true;
        }

        if (plan.CropWidth <= 0) return scaled;

        try
        {
            var cropX = Math.Max(0, (scaled.Width - plan.CropWidth) / 2);
            var cropY = Math.Max(0, (scaled.Height - plan.CropHeight) / 2);
            var rect = SKRectI.Create(cropX, cropY,
                Math.Min(plan.CropWidth, scaled.Width), Math.Min(plan.CropHeight, scaled.Height));
            var cropped = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(scaled, rect, new SKRect(0, 0, rect.Width, rect.Height));
            }
            return cropped;
        }
        finally
        {
            if (ownsScaled) scaled.Dispose();
        }
    }

    // ---------------------------------------------------------------- decode

    private static SKBitmap Decode(byte[] input, out ImageOutputFormat format, out double dpiX, out double dpiY)
    {
        ReadDpi(input, out dpiX, out dpiY);

        using (var codec = SKCodec.Create(new SKMemoryStream(input)))
        {
            if (codec is not null)
            {
                format = codec.EncodedFormat switch
                {
                    SKEncodedImageFormat.Jpeg => ImageOutputFormat.Jpeg,
                    SKEncodedImageFormat.Webp => ImageOutputFormat.Webp,
                    SKEncodedImageFormat.Bmp => ImageOutputFormat.Bmp,
                    SKEncodedImageFormat.Gif => ImageOutputFormat.Gif,
                    // Everything else (PNG, ICO, WBMP, raw...) falls back to PNG on Keep.
                    _ => ImageOutputFormat.Png,
                };
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var result = codec.GetPixels(info, bmp.GetPixels());
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                {
                    var oriented = ApplyOrigin(bmp, codec.EncodedOrigin);
                    if (!ReferenceEquals(oriented, bmp)) bmp.Dispose();
                    return oriented;
                }
                bmp.Dispose();
            }
        }

        return DecodeWithWpf(input, out format);
    }

    private static SKBitmap DecodeWithWpf(byte[] input, out ImageOutputFormat format)
    {
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(new MemoryStream(input), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The data is not a supported image.", ex);
        }

        var container = decoder.CodecInfo?.ContainerFormat ?? Guid.Empty;
        format = container == ContainerGuid.Tiff ? ImageOutputFormat.Tiff : ImageOutputFormat.Png;

        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var info = new SKImageInfo(converted.PixelWidth, converted.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        converted.CopyPixels(Int32Rect.Empty, bmp.GetPixels(), info.BytesSize, info.RowBytes);
        return bmp;
    }

    private static class ContainerGuid
    {
        public static readonly Guid Tiff = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");
    }

    private static void ReadDpi(byte[] input, out double dpiX, out double dpiY)
    {
        dpiX = DefaultDpi;
        dpiY = DefaultDpi;
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(input), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var f = decoder.Frames[0];
            if (f.DpiX > 0 && f.DpiY > 0) { dpiX = f.DpiX; dpiY = f.DpiY; }
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            // Not readable by WPF (WebP, for example): the default DPI stays.
        }
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return src;

        float w = src.Width, h = src.Height;
        var swap = origin >= SKEncodedOrigin.LeftTop;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => Mat(-1, 0, w, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Mat(-1, 0, w, 0, -1, h),
            SKEncodedOrigin.BottomLeft => Mat(1, 0, 0, 0, -1, h),
            SKEncodedOrigin.LeftTop => Mat(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Mat(0, -1, h, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Mat(0, -1, h, -1, 0, w),
            SKEncodedOrigin.LeftBottom => Mat(0, 1, 0, -1, 0, w),
            _ => SKMatrix.Identity,
        };

        var info = new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        var dst = new SKBitmap(info);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    // x' = a*x + b*y + tx ; y' = c*x + d*y + ty
    private static SKMatrix Mat(float a, float b, float tx, float c, float d, float ty)
        => new() { ScaleX = a, SkewX = b, TransX = tx, SkewY = c, ScaleY = d, TransY = ty, Persp0 = 0, Persp1 = 0, Persp2 = 1 };

    // ---------------------------------------------------------------- encode

    private static byte[] Encode(SKBitmap bmp, ImageOutputFormat format, int quality, out string extension)
    {
        quality = Math.Clamp(quality, 1, 100);
        switch (format)
        {
            case ImageOutputFormat.Jpeg:
            {
                extension = "jpg";
                using var flat = FlattenOnWhite(bmp);
                using var image = SKImage.FromBitmap(flat);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
                    ?? throw new InvalidOperationException("SkiaSharp failed to encode JPEG.");
                return data.ToArray();
            }
            case ImageOutputFormat.Webp:
            {
                extension = "webp";
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Webp, quality)
                    ?? throw new InvalidOperationException("SkiaSharp failed to encode WebP.");
                return data.ToArray();
            }
            case ImageOutputFormat.Bmp:
                extension = "bmp";
                return EncodeWpf(bmp, new BmpBitmapEncoder());
            case ImageOutputFormat.Tiff:
                extension = "tiff";
                return EncodeWpf(bmp, new TiffBitmapEncoder { Compression = TiffCompressOption.Zip });
            case ImageOutputFormat.Gif:
                extension = "gif";
                return EncodeWpf(bmp, new GifBitmapEncoder());
            default:
            {
                extension = "png";
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidOperationException("SkiaSharp failed to encode PNG.");
                return data.ToArray();
            }
        }
    }

    private static SKBitmap FlattenOnWhite(SKBitmap src)
    {
        var flat = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0);
        return flat;
    }

    private static byte[] EncodeWpf(SKBitmap bmp, BitmapEncoder encoder)
    {
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var stride = info.RowBytes;
        var pixels = new byte[stride * bmp.Height];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);

        var source = BitmapSource.Create(bmp.Width, bmp.Height, DefaultDpi, DefaultDpi, PixelFormats.Pbgra32, null, pixels, stride);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(converted));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
