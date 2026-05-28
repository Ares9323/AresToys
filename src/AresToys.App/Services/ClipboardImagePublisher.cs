using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services;

/// <summary>Helper for placing images on the Windows clipboard with proper alpha preservation.
/// <para>The naive <c>System.Windows.Clipboard.SetImage(bitmap)</c> publishes only
/// <c>CF_BITMAP</c>, which most Win32 consumers (Telegram, Discord, Slack, Chrome paste,
/// Office 365) interpret as 32-bit RGB without honouring the alpha channel — semi-transparent
/// pixels appear opaque on paste, which turned the Shadow effect's soft glow into a hard
/// neon-coloured shape on paste into Telegram.</para>
/// <para>The fix is to also publish the <em>PNG</em> clipboard format (a registered Windows
/// clipboard format string that all the alpha-aware apps prefer over the bitmap fallback).
/// We publish both so legacy consumers (Paint, Word) still get the bitmap path; modern
/// consumers grab the PNG and render the alpha correctly.</para></summary>
public static class ClipboardImagePublisher
{
    /// <summary>Publish <paramref name="pngBytes"/> on the clipboard. <em>Must</em> be called
    /// on the UI dispatcher thread (Win32 clipboard APIs are STA-only). Returns true on
    /// success, false on contention / clipboard-busy errors (those are non-fatal — the user
    /// can retry).
    /// <para>Format strategy: always publish "PNG" (alpha-correct). Additionally publish
    /// CF_BITMAP <em>only</em> when the source has no alpha channel. When alpha is present,
    /// the CF_BITMAP fallback would produce a white/black-bg flattened version and some
    /// modern apps (Telegram, etc.) pick CF_BITMAP first — making the cut-out paste with a
    /// solid background instead of the transparent PNG. Skipping CF_BITMAP for alpha images
    /// forces those apps onto the PNG path; legacy apps (Paint, older Word) lose this paste
    /// but they'd have flattened the alpha anyway.</para></summary>
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

        var data = new DataObject();
        // PNG: the registered clipboard format used by Chromium / Firefox / Telegram /
        // Discord / Slack / etc. for alpha-correct images. The MemoryStream must wrap the
        // bytes (the clipboard takes a copy when SetDataObject(..., copy:true) runs below,
        // so it's safe to dispose afterwards).
        var ms = new MemoryStream(pngBytes);
        data.SetData("PNG", ms);

        // Bitmap fallback ONLY when the PNG is fully opaque. See class doc above for why
        // alpha images must skip this — it saves modern apps from picking CF_BITMAP and
        // pasting the cutout with a flat white bg.
        if (!PngHasAlpha(pngBytes))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(pngBytes);
                bitmap.EndInit();
                bitmap.Freeze();
                data.SetImage(bitmap);
            }
            catch
            {
                // PNG decode failed — give up the legacy fallback, the modern PNG path is enough.
            }
        }

        try
        {
            // copy: true → the data is serialised to the clipboard and survives this process
            // exiting (so the user can paste in another app after closing AresToys).
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard contention: another app has the clipboard open. Caller can retry.
            return false;
        }
        finally
        {
            // The clipboard took a copy; safe to release our stream reference.
            ms.Dispose();
        }
    }

    /// <summary>Cheap PNG-header inspection: read the IHDR colour type byte to decide if
    /// alpha is plausibly present. PNG layout: 8-byte signature + 4-byte IHDR length +
    /// 4-byte "IHDR" tag + 13 data bytes (W, H, bitdepth, colourtype, compression, filter,
    /// interlace). Colour type byte sits at offset 25.
    /// <para>Colour types 4 (Grey+α) and 6 (RGBA) carry an alpha channel; type 3 (palette)
    /// can carry transparency via a tRNS chunk so we treat it conservatively as "has alpha".
    /// Types 0 (Grey) and 2 (RGB) are opaque-only. False positives (RGBA PNG with all
    /// alpha=255) are accepted because the cost is just losing the CF_BITMAP fallback for
    /// fully-opaque-but-RGBA captures, which is rare in practice and harmless.</para></summary>
    private static bool PngHasAlpha(byte[] pngBytes)
    {
        if (pngBytes.Length < 26) return true; // unknown / too short → conservative
        var colorType = pngBytes[25];
        return colorType == 3 || colorType == 4 || colorType == 6;
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
}
