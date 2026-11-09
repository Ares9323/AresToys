using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services;

/// <summary>Helper for placing images on the Windows clipboard with proper alpha preservation.
/// <para>The naive <c>System.Windows.Clipboard.SetImage(bitmap)</c> publishes only
/// <c>CF_BITMAP</c>, which most Win32 consumers interpret as 32-bit RGB without honouring the
/// alpha channel — semi-transparent pixels appear opaque on paste, which turned the Shadow
/// effect's soft glow into a hard neon-coloured shape on paste into Telegram.</para>
/// <para>We publish two clipboard formats natively in a single clipboard session:</para>
/// <list type="bullet">
///   <item><description><b>"PNG"</b> — a registered format string the alpha-aware apps
///   (Telegram, Firefox, Chrome's file paste) prefer.</description></item>
///   <item><description><b>CF_DIBV5</b> — a 32-bit DIB with an explicit alpha channel. This is
///   the format Chromium/Electron reads on <c>paste</c> into a chat box, so it's what makes the
///   image show up in <b>Discord</b> (which does <em>not</em> read the registered "PNG" format).
///   Windows auto-synthesises <c>CF_DIB</c> and <c>CF_BITMAP</c> from it for legacy consumers
///   (Paint, Office), so we don't publish those explicitly.</description></item>
/// </list>
/// <para>Publishing is done via raw Win32 (OpenClipboard/EmptyClipboard/SetClipboardData) rather
/// than <c>System.Windows.Clipboard</c> because WPF's <c>DataObject</c> can't emit the numeric
/// CF_DIBV5 (17) predefined format — it only maps named/string formats.</para></summary>
public static partial class ClipboardImagePublisher
{
    /// <summary>Publish <paramref name="pngBytes"/> on the clipboard. <em>Must</em> be called
    /// on the UI dispatcher thread (Win32 clipboard APIs are STA-only). Returns true on
    /// success, false on contention / clipboard-busy errors (those are non-fatal — the user
    /// can retry).
    /// <para>Format strategy: publish "PNG" (alpha-correct, preferred by Telegram/Firefox) and
    /// CF_DIBV5 (alpha-correct 32-bit DIB, the raster format Discord/Chromium actually reads on
    /// paste). Windows synthesises CF_DIB / CF_BITMAP from the DIBV5 for legacy consumers, so
    /// this single pair covers modern alpha-aware apps, Chromium/Electron, and old bitmap-only
    /// apps without flattening the alpha onto a solid background.</para></summary>
    public static bool SetPng(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0) return false;

        // Storage may hand us JPEG bytes (the capture pipeline's AutoJpeg setting re-encodes
        // PNGs over a size threshold into JPEG, still flagged ItemKind.Image). Publishing JFIF
        // bytes under the "PNG" clipboard format would silently fail in every consumer — they
        // read the PNG signature, see FF D8 instead, and treat the paste as no-op. Detect the
        // mismatch and re-encode through WIC so what we publish is always a real PNG.
        if (!HasPngSignature(pngBytes))
        {
            var converted = TryConvertToPng(pngBytes);
            if (converted is null) return false;
            pngBytes = converted;
        }

        // Build the CF_DIBV5 payload up-front (outside the clipboard-open window, which we want
        // to keep as short as possible). A decode failure isn't fatal: we still publish "PNG"
        // so the alpha-aware apps keep working; only Discord's raster path is lost.
        byte[]? dib = null;
        try
        {
            var (w, h, bgra) = DecodeToBgra32(pngBytes);
            if (w > 0 && h > 0) dib = BuildDibV5(w, h, bgra);
        }
        catch
        {
            // Decode/DIB build failed — fall through with dib == null.
        }

        // OpenClipboard(IntPtr.Zero) associates the clipboard with the current (UI/STA) thread.
        // Fails when another app currently holds the clipboard open → caller can retry.
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var published = false;

            var pngFormat = RegisterClipboardFormat("PNG");
            if (pngFormat != 0) published |= SetClipboardGlobal(pngFormat, pngBytes);

            if (dib is not null) published |= SetClipboardGlobal(CfDibV5, dib);

            return published;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Copy <paramref name="bytes"/> into a moveable HGLOBAL and hand it to the
    /// clipboard under <paramref name="format"/>. On success the OS owns the handle (it is freed
    /// when the clipboard is next emptied); on failure we free it ourselves. Must be called
    /// between OpenClipboard/EmptyClipboard and CloseClipboard.</summary>
    private static bool SetClipboardGlobal(uint format, byte[] bytes)
    {
        var hGlobal = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (hGlobal == IntPtr.Zero) return false;

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        // SetClipboardData transfers ownership of the handle to the system on success.
        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }
        return true;
    }

    /// <summary>Decode PNG bytes to a top-down, tightly-packed 32-bit BGRA pixel buffer plus its
    /// dimensions. Throws on decode failure (callers guard with try/catch).</summary>
    private static (int Width, int Height, byte[] Bgra) DecodeToBgra32(byte[] pngBytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(pngBytes);
        bmp.EndInit();
        bmp.Freeze();

        BitmapSource src = bmp.Format == PixelFormats.Bgra32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        if (!src.IsFrozen) src.Freeze();

        var width = src.PixelWidth;
        var height = src.PixelHeight;
        var stride = width * 4;
        var buf = new byte[stride * height];
        src.CopyPixels(buf, stride, 0);
        return (width, height, buf);
    }

    /// <summary>Build a CF_DIBV5 blob (BITMAPV5HEADER + pixel bits) from a top-down 32-bit BGRA
    /// buffer. The DIB is bottom-up (rows flipped), 32-bit, BI_BITFIELDS with an explicit alpha
    /// mask so alpha-aware consumers (Chromium/Discord) composite correctly. Public for tests.</summary>
    public static byte[] BuildDibV5(int width, int height, byte[] bgraTopDown)
    {
        const int HeaderSize = 124; // sizeof(BITMAPV5HEADER)
        var stride = width * 4;
        var image = new byte[stride * height];
        // DIBs are bottom-up: the first row in memory is the bottom image row. Flip vertically.
        for (var y = 0; y < height; y++)
        {
            Array.Copy(bgraTopDown, y * stride, image, (height - 1 - y) * stride, stride);
        }

        var buffer = new byte[HeaderSize + image.Length];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);
        w.Write(HeaderSize);                    // bV5Size
        w.Write(width);                         // bV5Width
        w.Write(height);                        // bV5Height (positive → bottom-up)
        w.Write((short)1);                      // bV5Planes
        w.Write((short)32);                     // bV5BitCount
        w.Write(3);                             // bV5Compression = BI_BITFIELDS
        w.Write(image.Length);                  // bV5SizeImage
        w.Write(0);                             // bV5XPelsPerMeter
        w.Write(0);                             // bV5YPelsPerMeter
        w.Write(0);                             // bV5ClrUsed
        w.Write(0);                             // bV5ClrImportant
        w.Write(0x00FF0000);                    // bV5RedMask
        w.Write(0x0000FF00);                    // bV5GreenMask
        w.Write(0x000000FF);                    // bV5BlueMask
        unchecked { w.Write((int)0xFF000000); } // bV5AlphaMask
        w.Write(0x73524742);                    // bV5CSType = LCS_sRGB ('sRGB')
        for (var i = 0; i < 9; i++) w.Write(0); // bV5Endpoints (CIEXYZTRIPLE = 9 LONGs)
        w.Write(0);                             // bV5GammaRed
        w.Write(0);                             // bV5GammaGreen
        w.Write(0);                             // bV5GammaBlue
        w.Write(4);                             // bV5Intent = LCS_GM_IMAGES
        w.Write(0);                             // bV5ProfileData
        w.Write(0);                             // bV5ProfileSize
        w.Write(0);                             // bV5Reserved
        w.Write(image);
        w.Flush();
        return buffer;
    }

    /// <summary>Cheap PNG-header inspection: returns true when the IHDR colour type byte
    /// indicates the PNG *could* carry alpha. PNG layout: 8-byte signature + 4-byte IHDR
    /// length + 4-byte "IHDR" tag + 13 data bytes (W, H, bitdepth, colourtype, compression,
    /// filter, interlace). Colour type byte sits at offset 25.
    /// <para>Types 4 (Grey+α) and 6 (RGBA) carry an alpha channel; type 3 (palette) can
    /// carry transparency via a tRNS chunk. Types 0 (Grey) and 2 (RGB) are opaque-only —
    /// returning false there lets <see cref="SetPng"/> skip the more-expensive pixel scan.
    /// A true return does NOT mean the image has actually-transparent pixels; combine with
    /// <see cref="BitmapHasAnyTransparentPixel"/> for the authoritative answer.</para></summary>
    public static bool PngHasAlphaFlag(byte[] pngBytes)
    {
        if (pngBytes.Length < 26) return true; // unknown / too short → conservative
        var colorType = pngBytes[25];
        return colorType == 3 || colorType == 4 || colorType == 6;
    }

    /// <summary>Scan the bitmap's alpha channel and return true on the first pixel with
    /// alpha &lt; 255. Used as the authoritative check after <see cref="PngHasAlphaFlag"/>
    /// flags a PNG as "possibly transparent" — WIC emits colorType=6 for every source
    /// from <c>System.Drawing.Bitmap(Format32bppArgb)</c> even when every alpha byte is
    /// 255, so the header heuristic alone over-rejects fully-opaque screenshots and
    /// breaks paste into Discord and other Win32-native consumers.
    /// <para>Cost is O(W × H) byte reads on the first scan, but the loop short-circuits
    /// on the first non-opaque pixel — a typical RGBA capture-with-transparency check is
    /// dominated by the format-convert + first-row copy, not the full scan. Allocates one
    /// row buffer (width × 4 bytes) instead of the full bitmap to keep memory bounded on
    /// 4K+ captures.</para></summary>
    public static bool BitmapHasAnyTransparentPixel(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        if (!bgra.IsFrozen) bgra.Freeze();
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        if (width <= 0 || height <= 0) return false;
        var stride = width * 4;
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            bgra.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            // BGRA layout → alpha sits at the 4th byte of each pixel quartet.
            for (var x = 3; x < stride; x += 4)
            {
                if (row[x] != 0xFF) return true;
            }
        }
        return false;
    }

    private static bool HasPngSignature(byte[] bytes)
        => bytes.Length >= 8 &&
           bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
           bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    /// <summary>Decode arbitrary image bytes via WIC (BitmapImage handles JPEG/GIF/BMP/TIFF)
    /// and re-encode as PNG. Used when the storage layer hands us a non-PNG payload (e.g.
    /// AutoJpeg-compressed capture) but the clipboard contract is "publish as PNG".
    /// Returns null on decode failure.</summary>
    private static byte[]? TryConvertToPng(byte[] sourceBytes)
    {
        try
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = new MemoryStream(sourceBytes);
            decoded.EndInit();
            decoded.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoded));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private const uint CfDibV5 = 17;      // CF_DIBV5 predefined clipboard format
    private const uint GmemMoveable = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}
