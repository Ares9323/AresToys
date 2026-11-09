using System.IO;
using System.Windows.Media.Imaging;
using AresToys.Core.Imaging;

namespace AresToys.App.Services;

/// <summary>WPF-backed <see cref="IImageEncoder"/> — uses the BitmapEncoder family from
/// PresentationCore (PngBitmapEncoder / JpegBitmapEncoder / BmpBitmapEncoder /
/// GifBitmapEncoder) so we don't have to ship a second imaging stack alongside SkiaSharp.
/// Skia's GIF/BMP encoders are spotty (Skia historically only decodes those); the WPF
/// encoders are ubiquitous on Windows + already part of the .NET runtime.</summary>
public sealed class WpfImageEncoder : IImageEncoder
{
    public byte[] Encode(byte[] sourceBytes, ImageFormat target, int jpegQuality = 90)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length == 0) throw new ArgumentException("Source bytes are empty", nameof(sourceBytes));

        // OnLoad caching frees the underlying stream immediately — without it the BitmapImage
        // keeps a handle on the MemoryStream and the next encode pass sees disposed data.
        using var input = new MemoryStream(sourceBytes, writable: false);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidOperationException("Source bytes contain no decodable frames.");

        var frame = decoder.Frames[0];
        BitmapEncoder encoder = target switch
        {
            ImageFormat.Png  => new PngBitmapEncoder(),
            ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ImageFormat.Bmp  => new BmpBitmapEncoder(),
            ImageFormat.Gif  => new GifBitmapEncoder(),
            _                => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(frame));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
