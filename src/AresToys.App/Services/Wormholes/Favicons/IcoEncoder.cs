using System.IO;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Turns arbitrary downloaded favicon bytes into a valid <c>.ico</c> so it can be
/// referenced from a <c>.url</c>'s <c>IconFile=</c> (Windows only accepts real icon containers
/// there, not a bare PNG). Already-<c>.ico</c> input passes straight through; anything WPF can
/// decode (PNG / JPEG / GIF / BMP) is re-encoded as PNG and wrapped in a single-entry ICO
/// (PNG-compressed entries are supported since Windows Vista). Undecodable input (notably SVG)
/// returns null so the caller falls back to the next favicon source.</summary>
public static class IcoEncoder
{
    /// <summary>Convert <paramref name="imageBytes"/> to ICO bytes, or null if they can't be
    /// decoded into a raster image.</summary>
    public static byte[]? ToIco(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length < 4) return null;
        if (IsIco(imageBytes)) return imageBytes;

        var png = ReencodeAsPng(imageBytes);
        if (png is null) return null;
        return WrapPngInIco(png);
    }

    /// <summary>ICO magic: reserved=0 (2 bytes), type=1 (2 bytes, little-endian).</summary>
    public static bool IsIco(byte[] b) =>
        b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00;

    /// <summary>Decode via WPF and re-encode the first frame as PNG. Returns null on any decode
    /// failure (corrupt bytes, unsupported format like SVG). Runs fine off the UI thread — the
    /// decoder works on an in-memory stream and the frame is consumed immediately.</summary>
    private static byte[]? ReencodeAsPng(byte[] imageBytes)
    {
        try
        {
            using var inStream = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(inStream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);
            using var outStream = new MemoryStream();
            encoder.Save(outStream);
            return outStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Build a one-entry ICO whose single image is the supplied PNG payload. Layout:
    /// 6-byte ICONDIR + 16-byte ICONDIRENTRY + PNG bytes (image data starts at offset 22).</summary>
    private static byte[] WrapPngInIco(byte[] png)
    {
        var (w, h) = ReadPngSize(png);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ICONDIR
        bw.Write((ushort)0);   // reserved
        bw.Write((ushort)1);   // type = 1 (icon)
        bw.Write((ushort)1);   // image count

        // ICONDIRENTRY — width/height stored as a single byte each; 0 means 256.
        bw.Write((byte)(w >= 256 ? 0 : w));
        bw.Write((byte)(h >= 256 ? 0 : h));
        bw.Write((byte)0);     // color palette count (0 = no palette)
        bw.Write((byte)0);     // reserved
        bw.Write((ushort)1);   // color planes
        bw.Write((ushort)32);  // bits per pixel
        bw.Write((uint)png.Length);   // size of the image data
        bw.Write((uint)22);           // offset of the image data (6 + 16)

        bw.Write(png);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Pull width/height out of a PNG IHDR chunk. PNG signature is 8 bytes; IHDR length
    /// (4) + "IHDR" (4) follow, then width (4, big-endian) + height (4, big-endian). Falls back
    /// to 32×32 if the bytes are too short to parse (shouldn't happen for our own re-encode).</summary>
    private static (int w, int h) ReadPngSize(byte[] png)
    {
        if (png.Length < 24) return (32, 32);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        if (w <= 0 || h <= 0) return (32, 32);
        return (w, h);
    }
}
