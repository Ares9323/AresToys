using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services.Peek;

/// <summary>A flip and a quarter-turn rotation, applied in that order (the flip first, then the rotation clockwise):
/// what Peek's rotate and flip buttons leave the view in. The eight values are the eight EXIF orientations.</summary>
internal readonly record struct PeekTurn(bool Flip, int Rotation)
{
    public static PeekTurn None => new(false, 0);

    public bool IsNone => !Flip && Rotation == 0;

    /// <summary>This turn followed by <paramref name="next"/>. A rotation that comes after a flip turns the other way
    /// round: Rotate(a) after Flip is Flip after Rotate(-a).</summary>
    public PeekTurn Then(PeekTurn next) => new(Flip != next.Flip, Normalize(next.Rotation + (next.Flip ? -Rotation : Rotation)));

    /// <summary>The turn that undoes this one (a flip with any rotation is its own inverse).</summary>
    public PeekTurn Inverse => Flip ? this : new PeekTurn(false, Normalize(-Rotation));

    private static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;
}

/// <summary>The one writer of an image back to its file: what Peek does when the user rotates or flips an image
/// (ruling 1965, the way the Windows Photos app saves a turn). JPEG is turned without touching a pixel, by writing the
/// EXIF Orientation tag (metadata and quality stay as they are); PNG, BMP, GIF (one frame) and TIFF (one page) are
/// decoded, turned and encoded again with the same encoder, keeping the metadata the encoder takes. Any other format
/// (WebP, HEIC, animated GIF, ICO...) has no writer here: <see cref="CanSave"/> is false and the turn is view only.
/// The file is replaced through a temp file in its folder, so a failed write leaves it as it was. Meant for a
/// thread-pool thread; writes are one at a time.</summary>
internal static class PeekImageSave
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlushNoWait = 0x3000;
    private const ushort OrientationTag = 0x0112;
    private const string TiffOrientationQuery = "/ifd/{ushort=274}";

    private static readonly object WriteLock = new();
    private static readonly Guid JpegId = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    private static readonly Guid PngId = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid BmpId = new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3");
    private static readonly Guid TiffId = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");
    private static readonly Guid GifId = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");

    /// <summary>The turn each EXIF orientation (1 to 8, by index plus one) puts on the stored pixels; the same table
    /// <see cref="PeekImageLoader"/> uses to show the image upright.</summary>
    private static readonly PeekTurn[] OrientationTurns =
    [
        new(false, 0), new(true, 0), new(false, 180), new(true, 180), new(true, 270), new(false, 90), new(true, 90), new(false, 270),
    ];

    private enum Format { None, Jpeg, Png, Bmp, Tiff, Gif }

    /// <summary>Whether a turn of the image at <paramref name="path"/> can be written back: a JPEG, PNG, BMP, single
    /// frame GIF or single page TIFF that WIC reads. Reads the file header only.</summary>
    public static bool CanSave(string path) => Probe(path) != Format.None;

    /// <summary>Writes the image at <paramref name="path"/> turned by <paramref name="turn"/>, which is relative to the
    /// image as <see cref="PeekImageLoader"/> shows it (the EXIF orientation already applied). True when the file now
    /// holds the turned image; false when it could not be written (read only, locked, no rights, not a format
    /// <see cref="CanSave"/> accepts), in which case the file is untouched. Keeps the creation time, then tells
    /// Explorer the item changed so its thumbnail is rebuilt.</summary>
    public static bool Save(string path, PeekTurn turn)
    {
        if (turn.IsNone)
        {
            return true;
        }

        lock (WriteLock)
        {
            string? temp = null;
            try
            {
                try
                {
                    var format = Probe(path);
                    var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (format == Format.None || folder is null)
                    {
                        return false;
                    }

                    var created = File.GetCreationTimeUtc(path);
                    temp = Path.Combine(folder, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".peek-tmp");
                    var written = format == Format.Jpeg ? WriteJpeg(path, temp, turn) : WriteEncoded(path, temp, format, turn);
                    if (!written)
                    {
                        return false;
                    }

                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    temp = null;
                    File.SetCreationTimeUtc(path, created);
                    SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlushNoWait, path, IntPtr.Zero);
                    return true;
                }
                finally
                {
                    if (temp is not null)
                    {
                        File.Delete(temp);
                    }
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Read only, locked, no rights, a file WIC cannot encode: the caller shows "could not save" and the
                // original is still in place (it is only replaced by the last step).
                return false;
            }
        }
    }

    private static bool IsExpected(Exception ex) => ex is NotSupportedException or FileFormatException or InvalidOperationException
        or IOException or UnauthorizedAccessException or COMException or Win32Exception or ArgumentException;

    private static Format Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var id = decoder.CodecInfo.ContainerFormat;

            // The loader applies the EXIF orientation of a JPEG or TIFF by extension only: a turn of another name
            // could not be told apart from the orientation Peek ignored, so it stays view only.
            var format = id == JpegId && HasExtension(path, ".jpg", ".jpeg") ? Format.Jpeg
                : id == PngId ? Format.Png
                : id == BmpId ? Format.Bmp
                : id == TiffId && HasExtension(path, ".tif", ".tiff") ? Format.Tiff
                : id == GifId ? Format.Gif
                : Format.None;
            return format is Format.Tiff or Format.Gif && decoder.Frames.Count != 1 ? Format.None : format;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // No codec, a damaged or locked file: nothing to write back.
            return Format.None;
        }
    }

    private static bool HasExtension(string path, string first, string second)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, first, StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, second, StringComparison.OrdinalIgnoreCase);
    }

    private static PeekTurn FromOrientation(int orientation) => OrientationTurns[orientation - 1];

    private static int ToOrientation(PeekTurn turn) => Array.IndexOf(OrientationTurns, turn) + 1;

    // PNG, BMP, GIF, TIFF: decode the first frame in full, turn the pixels, encode again.

    private static bool WriteEncoded(string path, string temp, Format format, PeekTurn turn)
    {
        // The stream stays open until the new file is written: the metadata is read from it lazily.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            return false;
        }

        var frame = decoder.Frames[0];

        // A TIFF stored sideways is shown upright by its orientation tag: the turn is relative to that, and the pixels
        // written are the upright ones, so the tag goes back to 1.
        var total = (format == Format.Tiff ? FromOrientation(ReadTiffOrientation(frame)) : PeekTurn.None).Then(turn);
        BitmapSource pixels = frame;
        if (!total.IsNone)
        {
            var transform = new TransformGroup();
            if (total.Flip)
            {
                transform.Children.Add(new ScaleTransform(-1, 1));
            }

            if (total.Rotation != 0)
            {
                transform.Children.Add(new RotateTransform(total.Rotation));
            }

            pixels = new TransformedBitmap(frame, transform);
        }

        try
        {
            Encode(temp, format, pixels, frame, keepMetadata: true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or COMException)
        {
            // The encoder takes none of this file's metadata (BMP has none, some PNG and GIF blocks are refused): the
            // turned pixels are written alone rather than not at all.
            Encode(temp, format, pixels, frame, keepMetadata: false);
        }

        return true;
    }

    private static void Encode(string temp, Format format, BitmapSource pixels, BitmapFrame source, bool keepMetadata)
    {
        BitmapEncoder encoder = format switch
        {
            Format.Png => new PngBitmapEncoder(),
            Format.Bmp => new BmpBitmapEncoder(),
            Format.Tiff => new TiffBitmapEncoder(),
            _ => new GifBitmapEncoder(),
        };
        var metadata = keepMetadata ? CopyMetadata(source, format) : null;
        encoder.Frames.Add(BitmapFrame.Create(pixels, null, metadata, source.ColorContexts));
        using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static BitmapMetadata? CopyMetadata(BitmapFrame source, Format format)
    {
        if (source.Metadata is not BitmapMetadata metadata)
        {
            return null;
        }

        var copy = metadata.Clone();
        if (format == Format.Tiff && copy.ContainsQuery(TiffOrientationQuery))
        {
            copy.SetQuery(TiffOrientationQuery, (ushort)1);
        }

        return copy;
    }

    private static int ReadTiffOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(TiffOrientationQuery)
                && metadata.GetQuery(TiffOrientationQuery) is ushort value && value is >= 1 and <= 8)
            {
                return value;
            }
        }
        catch (NotSupportedException)
        {
            // The codec exposes no metadata for this frame: the picture is upright, as the loader shows it.
        }

        return 1;
    }

    // JPEG: the pixels stay as they are, only the EXIF Orientation tag changes.

    private static bool WriteJpeg(string path, string temp, PeekTurn turn)
    {
        var turned = TurnJpeg(File.ReadAllBytes(path), turn);
        if (turned is null)
        {
            return false;
        }

        File.WriteAllBytes(temp, turned);
        return true;
    }

    /// <summary>The JPEG <paramref name="data"/> with its EXIF Orientation set so that the image, shown upright by the
    /// tag, is turned by <paramref name="turn"/> more. The tag is edited in place; when the Exif block has none, its
    /// first IFD is rewritten at the end of the block with the tag added (every other offset stays valid); when the
    /// file has no Exif block, a minimal one is inserted after the JFIF header. Null when the headers are not what
    /// this expects (nothing is written then).</summary>
    private static byte[]? TurnJpeg(byte[] data, PeekTurn turn)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var exifAt = -1;
        var insertAt = 2;
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF)
            {
                return null;
            }

            var marker = data[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }

            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8)
            {
                pos += 2;
                continue;
            }

            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2 || pos + 2 + length > data.Length)
            {
                return null;
            }

            if (marker == 0xE1 && length >= 8 && IsExifHeader(data, pos + 4))
            {
                if (exifAt < 0)
                {
                    exifAt = pos;
                }
            }
            else if (marker == 0xE0 && pos == 2)
            {
                insertAt = pos + 2 + length;
            }

            pos += 2 + length;
        }

        return exifAt < 0 ? InsertExif(data, insertAt, turn) : TurnExif(data, exifAt, turn);
    }

    private static bool IsExifHeader(byte[] data, int at)
        => data[at] == (byte)'E' && data[at + 1] == (byte)'x' && data[at + 2] == (byte)'i' && data[at + 3] == (byte)'f'
           && data[at + 4] == 0 && data[at + 5] == 0;

    private static byte[] InsertExif(byte[] data, int insertAt, PeekTurn turn)
    {
        var orientation = ToOrientation(PeekTurn.None.Then(turn));
        byte[] segment =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        var result = new byte[data.Length + segment.Length];
        Buffer.BlockCopy(data, 0, result, 0, insertAt);
        Buffer.BlockCopy(segment, 0, result, insertAt, segment.Length);
        Buffer.BlockCopy(data, insertAt, result, insertAt + segment.Length, data.Length - insertAt);
        return result;
    }

    private static byte[]? TurnExif(byte[] data, int exifAt, PeekTurn turn)
    {
        var length = (data[exifAt + 2] << 8) | data[exifAt + 3];
        var tiff = exifAt + 10;
        var tiffLength = length - 8;
        var segmentEnd = exifAt + 2 + length;
        if (tiffLength < 8)
        {
            return null;
        }

        bool big;
        if (data[tiff] == (byte)'M' && data[tiff + 1] == (byte)'M')
        {
            big = true;
        }
        else if (data[tiff] == (byte)'I' && data[tiff + 1] == (byte)'I')
        {
            big = false;
        }
        else
        {
            return null;
        }

        var ifd = (int)ReadUInt32(data, tiff + 4, big);
        if (ReadUInt16(data, tiff + 2, big) != 42 || ifd < 8 || ifd + 2 > tiffLength)
        {
            return null;
        }

        var count = ReadUInt16(data, tiff + ifd, big);
        var entries = tiff + ifd + 2;
        if (ifd + 2 + count * 12 + 4 > tiffLength)
        {
            return null;
        }

        int insertBefore = count;
        for (var i = 0; i < count; i++)
        {
            var entry = entries + i * 12;
            var tag = ReadUInt16(data, entry, big);
            if (tag == OrientationTag)
            {
                var current = ReadUInt16(data, entry + 8, big);
                if (ReadUInt16(data, entry + 2, big) != 3 || ReadUInt32(data, entry + 4, big) != 1 || current is < 1 or > 8)
                {
                    return null;
                }

                var edited = (byte[])data.Clone();
                WriteUInt16(edited, entry + 8, (ushort)ToOrientation(FromOrientation(current).Then(turn)), big);
                return edited;
            }

            if (tag > OrientationTag && insertBefore == count)
            {
                insertBefore = i;
            }
        }

        // No Orientation tag: IFD0 is copied after the Exif block with the tag added, and the header points to the copy.
        var pad = tiffLength % 2;
        var newIfdSize = 2 + (count + 1) * 12 + 4;
        if (length + pad + newIfdSize > 0xFFFF)
        {
            return null;
        }

        var result = new byte[data.Length + pad + newIfdSize];
        Buffer.BlockCopy(data, 0, result, 0, segmentEnd);
        var copy = segmentEnd + pad;
        WriteUInt16(result, copy, (ushort)(count + 1), big);
        var write = copy + 2;
        for (var i = 0; i <= count; i++)
        {
            if (i == insertBefore)
            {
                WriteUInt16(result, write, OrientationTag, big);
                WriteUInt16(result, write + 2, 3, big);
                WriteUInt32(result, write + 4, 1, big);
                WriteUInt16(result, write + 8, (ushort)ToOrientation(PeekTurn.None.Then(turn)), big);
                write += 12;
            }

            if (i < count)
            {
                Buffer.BlockCopy(data, entries + i * 12, result, write, 12);
                write += 12;
            }
        }

        Buffer.BlockCopy(data, entries + count * 12, result, write, 4);
        Buffer.BlockCopy(data, segmentEnd, result, segmentEnd + pad + newIfdSize, data.Length - segmentEnd);
        WriteUInt32(result, tiff + 4, (uint)(tiffLength + pad), big);
        var newLength = length + pad + newIfdSize;
        result[exifAt + 2] = (byte)(newLength >> 8);
        result[exifAt + 3] = (byte)(newLength & 0xFF);
        return result;
    }

    private static ushort ReadUInt16(byte[] data, int at, bool big)
        => big ? (ushort)((data[at] << 8) | data[at + 1]) : (ushort)(data[at] | (data[at + 1] << 8));

    private static uint ReadUInt32(byte[] data, int at, bool big)
        => big
            ? ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3]
            : data[at] | ((uint)data[at + 1] << 8) | ((uint)data[at + 2] << 16) | ((uint)data[at + 3] << 24);

    private static void WriteUInt16(byte[] data, int at, ushort value, bool big)
    {
        data[at] = (byte)(big ? value >> 8 : value & 0xFF);
        data[at + 1] = (byte)(big ? value & 0xFF : value >> 8);
    }

    private static void WriteUInt32(byte[] data, int at, uint value, bool big)
    {
        for (var i = 0; i < 4; i++)
        {
            var shift = big ? 24 - 8 * i : 8 * i;
            data[at + i] = (byte)(value >> shift);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
}
