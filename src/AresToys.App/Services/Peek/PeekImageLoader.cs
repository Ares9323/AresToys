using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services.Peek;

/// <summary>What Peek knows about one file after a decode attempt: the bitmap (null when the extension is
/// not an image Peek previews or WIC could not read the file), the pixel size of the image as displayed
/// (after the EXIF rotation, before any downscale), and the size of the file in bytes (-1 when unreadable).</summary>
internal sealed record PeekImageInfo(BitmapSource? Bitmap, int PixelWidth, int PixelHeight, long FileBytes);

/// <summary>The WIC decode behind the Peek window. Every method here is meant for a thread-pool thread:
/// <see cref="LoadAsync"/> and <see cref="LoadWithInfoAsync"/> hop there themselves, so the UI thread only
/// awaits them. The returned bitmaps are frozen and safe to hand to the dispatcher thread.</summary>
internal static class PeekImageLoader
{
    /// <summary>Decodes the image at <paramref name="path"/> with its EXIF rotation applied, downscaled at decode
    /// time so its longest side is at most <paramref name="maxPixelWidth"/> (never enlarged). Null when the file
    /// is not a supported image or cannot be read. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled.</summary>
    public static async Task<BitmapSource?> LoadAsync(string path, int maxPixelWidth, CancellationToken ct)
    {
        var info = await LoadWithInfoAsync(path, maxPixelWidth, ct).ConfigureAwait(false);
        return info.Bitmap;
    }

    /// <summary>Same decode as <see cref="LoadAsync"/>, with the displayed pixel size and the file size the
    /// Peek footer shows. The size of the file is filled in even when the bitmap is null.</summary>
    public static Task<PeekImageInfo> LoadWithInfoAsync(string path, int maxPixelWidth, CancellationToken ct)
        => Task.Run(() => Load(path, maxPixelWidth, ct), ct);

    private static PeekImageInfo Load(string path, int maxPixelWidth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = ReadLength(path);
        if (PeekPreview.For(path) != PeekPreviewKind.Image)
        {
            return new PeekImageInfo(null, 0, 0, bytes);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            // Icons hold several sizes: take the largest frame as it is (icons are small, no downscale needed).
            if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
            {
                var icon = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource best = icon.Frames.OrderByDescending(f => f.PixelWidth).First();
                if (best.CanFreeze) best.Freeze();
                return new PeekImageInfo(best, best.PixelWidth, best.PixelHeight, bytes);
            }

            // Header only: pixel size and EXIF orientation, without decoding the pixels.
            var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = probe.Frames[0];
            var sourceWidth = frame.PixelWidth;
            var sourceHeight = frame.PixelHeight;
            var orientation = ReadOrientation(frame, Path.GetExtension(path));
            ct.ThrowIfCancellationRequested();

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            var longest = Math.Max(sourceWidth, sourceHeight);
            if (maxPixelWidth > 0 && longest > maxPixelWidth)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(sourceWidth * (double)maxPixelWidth / longest));
            }

            image.EndInit();
            image.Freeze();
            ct.ThrowIfCancellationRequested();

            BitmapSource result = image;
            var transform = OrientationTransform(orientation);
            if (transform is not null)
            {
                var rotated = new TransformedBitmap(image, transform);
                rotated.Freeze();
                result = rotated;
            }

            var swapped = orientation >= 5;
            return new PeekImageInfo(result, swapped ? sourceHeight : sourceWidth, swapped ? sourceWidth : sourceHeight, bytes);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or COMException
                                       or Win32Exception or ArgumentException)
        {
            // WIC has no codec for the file (for example .webp without the Windows WebP extension), the file is
            // damaged, or it is locked: Peek shows the file name over an empty area, the caller sees a null bitmap.
            return new PeekImageInfo(null, 0, 0, bytes);
        }
    }

    private static long ReadLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable: the footer simply omits the size.
            return -1;
        }
    }

    /// <summary>EXIF orientation (1 to 8) of a jpeg or tiff frame; 1 when there is none or the codec has no metadata.</summary>
    private static int ReadOrientation(BitmapFrame frame, string extension)
    {
        string? query = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "/app1/ifd/{ushort=274}",
            ".tif" or ".tiff" => "/ifd/{ushort=274}",
            _ => null,
        };
        if (query is null) return 1;

        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(query)
                && metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8)
            {
                return value;
            }
        }
        catch (NotSupportedException)
        {
            // The codec exposes no metadata for this frame: treat the picture as upright.
        }

        return 1;
    }

    /// <summary>The flip and rotation that turn a bitmap stored with the given EXIF orientation upright; null for 1.</summary>
    private static Transform? OrientationTransform(int orientation) => orientation switch
    {
        2 => new ScaleTransform(-1, 1),
        3 => new RotateTransform(180),
        4 => new ScaleTransform(1, -1),
        5 => Group(new RotateTransform(90), new ScaleTransform(-1, 1)),
        6 => new RotateTransform(90),
        7 => Group(new RotateTransform(270), new ScaleTransform(-1, 1)),
        8 => new RotateTransform(270),
        _ => null,
    };

    private static TransformGroup Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms)
        {
            group.Children.Add(transform);
        }

        return group;
    }
}
