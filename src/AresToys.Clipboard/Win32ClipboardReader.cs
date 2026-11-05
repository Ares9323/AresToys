using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Clipboard.Native;

namespace AresToys.Clipboard;

[SupportedOSPlatform("windows")]
public sealed class Win32ClipboardReader : IClipboardReader
{
    // Size limits, so one huge copy (an Excel range, a browser page, a giant image) cannot
    // stall the reader or fill memory. Anything over a limit is capped or skipped, and logged.

    /// <summary>Text, HTML and RTF payloads above this many bytes are not stored whole: the
    /// Unicode text is stored instead (capped at this size), or the copy is skipped when there is none.</summary>
    private const long MaxTextBytes = 16L * 1024 * 1024;

    /// <summary>Previews and search text are built from at most this much of the payload.</summary>
    private const int PreviewSourceBytes = 64 * 1024;

    /// <summary>Largest picture (width x height) converted from CF_DIB; above it the copy is skipped.</summary>
    private const long MaxDibPixels = 100_000_000;

    /// <summary>Another app can hold the clipboard open for a moment right after copying;
    /// the read waits and tries again a few times before giving up.</summary>
    private const int OpenAttempts = 5;
    private const int OpenRetryDelayMs = 20;

    private readonly ILogger<Win32ClipboardReader> _logger;
    private readonly uint _cfHtml;
    private readonly uint _cfRtf;
    private readonly uint _cfPng;

    public Win32ClipboardReader(ILogger<Win32ClipboardReader>? logger = null)
    {
        _logger = logger ?? NullLogger<Win32ClipboardReader>.Instance;
        _cfHtml = ClipboardNativeMethods.RegisterClipboardFormat("HTML Format");
        _cfRtf = ClipboardNativeMethods.RegisterClipboardFormat("Rich Text Format");
        _cfPng = ClipboardNativeMethods.RegisterClipboardFormat("PNG");
    }

    public ClipboardChange? ReadCurrent(IntPtr ownerHwnd, ForegroundSnapshot foreground, DateTimeOffset capturedAt)
    {
        if (!OpenClipboardWithRetry(ownerHwnd))
        {
            _logger.LogWarning("Clipboard could not be opened after {Attempts} attempts (held by another app)", OpenAttempts);
            return null;
        }
        try
        {
            var (format, payload, preview, files) = ReadFirstAvailable();
            if (format == ClipboardFormat.None) return null;

            return new ClipboardChange(
                Format: format,
                CapturedAt: capturedAt,
                SourceProcess: foreground.ProcessName,
                SourceWindow: foreground.WindowTitle,
                Payload: payload,
                PreviewText: preview,
                FilePaths: files);
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }
    }

    private static bool OpenClipboardWithRetry(IntPtr ownerHwnd)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (ClipboardNativeMethods.OpenClipboard(ownerHwnd)) return true;
            if (attempt < OpenAttempts - 1) Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    private (ClipboardFormat Format, ReadOnlyMemory<byte> Payload, string? Preview, string[]? Files) ReadFirstAvailable()
    {
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfHdrop))
        {
            var files = ReadFileList();
            if (files is not null && files.Length > 0)
                return (ClipboardFormat.Files, ReadOnlyMemory<byte>.Empty, string.Join(Environment.NewLine, files), files);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfHtml))
        {
            var bytes = ReadGlobalBytes(_cfHtml, MaxTextBytes, out var size);
            if (bytes is null) return UnicodeTextInstead("HTML", size);
            // A browser "Copy image" puts only an <img> in CF_HTML next to the picture: with no visible text
            // that is the picture, so it becomes an Image. If the picture cannot be read, the Html item stays.
            if (HasPictureFormat() && ExtractHtmlPreview(bytes).Length == 0 && TryReadPicture() is { } picture)
                return (ClipboardFormat.Image, picture, "[image]", null);
            // Prefer CF_UNICODETEXT (UTF-16, always clean) for the preview when available — some
            // sources put double-encoded UTF-8 in CF_HTML and our extractor would inherit the
            // mojibake. Fall back to extracting plaintext from the HTML body otherwise.
            var preview = ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText)
                ? TruncatePreview(ReadUnicodeText(PreviewSourceBytes / 2, out _))
                : ExtractHtmlPreview(bytes);
            return (ClipboardFormat.Html, bytes, preview, null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfRtf))
        {
            var bytes = ReadGlobalBytes(_cfRtf, MaxTextBytes, out var size);
            if (bytes is null) return UnicodeTextInstead("RTF", size);
            var preview = ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText)
                ? TruncatePreview(ReadUnicodeText(PreviewSourceBytes / 2, out _))
                : ExtractRtfPreview(bytes);
            return (ClipboardFormat.Rtf, bytes, preview, null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText))
        {
            var text = ReadUnicodeText((int)(MaxTextBytes / 2), out var truncated);
            if (truncated)
                _logger.LogWarning("Clipboard text is over the {Limit} byte limit: stored capped", MaxTextBytes);
            return TextResult(text);
        }
        // The registered "PNG" format (browsers, image editors, AresToys itself) keeps the alpha
        // channel; CF_DIB goes through a BMP decode that drops it, so a transparent image would
        // come back with a black or white background.
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfPng))
        {
            var pngBytes = ReadGlobalBytes(_cfPng, Array.MaxLength, out var size);
            if (pngBytes is null)
            {
                _logger.LogWarning("Clipboard PNG skipped: {Bytes} bytes is over the array limit", size);
                return (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
            }
            if (IsPng(pngBytes)) return (ClipboardFormat.Image, pngBytes, "[image]", null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfDib))
        {
            var dibBytes = ReadDibBytes();
            var pngBytes = dibBytes is null ? null : TryConvertDibToPng(dibBytes);
            return pngBytes is not null
                ? (ClipboardFormat.Image, pngBytes, "[image]", null)
                : (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
        }
        return (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
    }

    private bool HasPictureFormat()
        => ClipboardNativeMethods.IsClipboardFormatAvailable(_cfPng)
           || ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfDib);

    /// <summary>The picture of the copy as PNG bytes: the registered PNG format first, then CF_DIB converted.
    /// Null when neither reads. Called with the clipboard open.</summary>
    private byte[]? TryReadPicture()
    {
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfPng))
        {
            var pngBytes = ReadGlobalBytes(_cfPng, Array.MaxLength, out _);
            if (pngBytes is not null && IsPng(pngBytes)) return pngBytes;
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfDib))
        {
            var dibBytes = ReadDibBytes();
            return dibBytes is null ? null : TryConvertDibToPng(dibBytes);
        }
        return null;
    }

    /// <summary>Plain text item. The text is also the search text, indexed from its first 64 KB only.</summary>
    private static (ClipboardFormat Format, ReadOnlyMemory<byte> Payload, string? Preview, string[]? Files) TextResult(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var preview = text.Length > PreviewSourceBytes ? text[..PreviewSourceBytes] : text;
        return (ClipboardFormat.Text, bytes, preview, null);
    }

    /// <summary>An HTML or RTF payload over the limit: keep only its Unicode text, or skip the copy when there is none.</summary>
    private (ClipboardFormat Format, ReadOnlyMemory<byte> Payload, string? Preview, string[]? Files) UnicodeTextInstead(string label, long size)
    {
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText))
        {
            var text = ReadUnicodeText((int)(MaxTextBytes / 2), out var truncated);
            if (text.Length > 0)
            {
                _logger.LogWarning("Clipboard {Label} of {Bytes} bytes is over the {Limit} byte limit: stored as plain text only{Capped}",
                    label, size, MaxTextBytes, truncated ? " (text capped too)" : string.Empty);
                return TextResult(text);
            }
        }
        _logger.LogWarning("Clipboard {Label} of {Bytes} bytes is over the {Limit} byte limit and has no text: skipped", label, size, MaxTextBytes);
        return (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    /// <summary>Copies the clipboard block of a format. Null when it is bigger than <paramref name="maxBytes"/>
    /// (its size is still reported); an empty array when there is no data.</summary>
    private static byte[]? ReadGlobalBytes(uint format, long maxBytes, out long size)
    {
        size = 0;
        var hMem = ClipboardNativeMethods.GetClipboardData(format);
        if (hMem == IntPtr.Zero) return [];
        size = (long)ClipboardNativeMethods.GlobalSize(hMem);
        if (size <= 0) return [];
        if (size > maxBytes || size > Array.MaxLength) return null;
        var ptr = ClipboardNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return [];
        try
        {
            var buffer = new byte[size];
            Marshal.Copy(ptr, buffer, 0, (int)size);
            return buffer;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(hMem);
        }
    }

    /// <summary>CF_DIB bytes, read only when the picture is at most <see cref="MaxDibPixels"/>:
    /// the size is taken from the header before anything is copied. Null when over the limit.</summary>
    private byte[]? ReadDibBytes()
    {
        var hMem = ClipboardNativeMethods.GetClipboardData(ClipboardNativeMethods.CfDib);
        if (hMem == IntPtr.Zero) return [];
        var size = (long)ClipboardNativeMethods.GlobalSize(hMem);
        if (size <= 0) return [];
        var ptr = ClipboardNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return [];
        try
        {
            if (size >= 12)
            {
                // BITMAPINFOHEADER: biWidth at 4, biHeight at 8 (negative = top-down).
                var pixels = Math.Abs((long)Marshal.ReadInt32(ptr, 4)) * Math.Abs((long)Marshal.ReadInt32(ptr, 8));
                if (pixels > MaxDibPixels)
                {
                    _logger.LogWarning("Clipboard picture of {Pixels} pixels is over the {Limit} pixel limit: skipped", pixels, MaxDibPixels);
                    return null;
                }
            }
            if (size > Array.MaxLength)
            {
                _logger.LogWarning("Clipboard picture of {Bytes} bytes is over the array limit: skipped", size);
                return null;
            }
            var buffer = new byte[size];
            Marshal.Copy(ptr, buffer, 0, (int)size);
            return buffer;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(hMem);
        }
    }

    /// <summary>Reads CF_UNICODETEXT, at most <paramref name="maxChars"/> characters.
    /// <paramref name="truncated"/> is true when the clipboard held more.</summary>
    private static string ReadUnicodeText(int maxChars, out bool truncated)
    {
        truncated = false;
        var hMem = ClipboardNativeMethods.GetClipboardData(ClipboardNativeMethods.CfUnicodeText);
        if (hMem == IntPtr.Zero) return string.Empty;
        var held = (long)ClipboardNativeMethods.GlobalSize(hMem) / 2;
        if (held <= 0) return string.Empty;
        var ptr = ClipboardNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return string.Empty;
        try
        {
            var count = (int)Math.Min(held, maxChars);
            var text = Marshal.PtrToStringUni(ptr, count);
            var nul = text.IndexOf('\0');
            if (nul >= 0) return text[..nul];
            if (held > maxChars)
            {
                truncated = true;
                // Do not cut a surrogate pair in half.
                if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
            }
            return text;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(hMem);
        }
    }

    private static string[]? ReadFileList()
    {
        var hDrop = ClipboardNativeMethods.GetClipboardData(ClipboardNativeMethods.CfHdrop);
        if (hDrop == IntPtr.Zero) return null;

        var count = ClipboardNativeMethods.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0) return null;

        var paths = new string[count];
        var buffer = new char[260];
        for (uint i = 0; i < count; i++)
        {
            var len = ClipboardNativeMethods.DragQueryFile(hDrop, i, buffer, (uint)buffer.Length);
            paths[i] = new string(buffer, 0, (int)len);
        }
        return paths;
    }

    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > 256 ? text[..256] + "…" : text;
    }

    /// <summary>The first <see cref="PreviewSourceBytes"/> of a payload as text, so the preview
    /// regexes never run over a multi-megabyte page.</summary>
    private static string PreviewSourceText(byte[] bytes)
        => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, PreviewSourceBytes));

    /// <summary>Strips CF_HTML preamble + tags so the row preview / FTS index sees readable text
    /// instead of "Version:0.9 StartHTML:..." gibberish.</summary>
    private static string ExtractHtmlPreview(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var text = PreviewSourceText(bytes);

        var fragStart = text.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragStart >= 0) text = text[(fragStart + "<!--StartFragment-->".Length)..];
        var fragEnd = text.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragEnd >= 0) text = text[..fragEnd];

        // No marker — drop the key:value preamble lines (Version:, StartHTML:, etc.) by skipping to
        // the first '<' if a known preamble key is present before it.
        if (fragStart < 0)
        {
            var firstTag = text.IndexOf('<');
            if (firstTag > 0)
            {
                var head = text[..firstTag];
                if (head.Contains("Version:", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("StartFragment:", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[firstTag..];
                }
            }
        }

        text = Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 256) text = text[..256] + "…";
        return text;
    }

    /// <summary>Strips RTF control words so the row preview / FTS sees readable text.</summary>
    private static string ExtractRtfPreview(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var text = PreviewSourceText(bytes);
        // \word, \word123, \'hh hex escapes, then strip braces and collapse whitespace.
        text = Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", " ");
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", " ");
        text = text.Replace("{", " ").Replace("}", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 256) text = text[..256] + "…";
        return text;
    }

    /// <summary>
    /// CF_DIB = BITMAPINFOHEADER + (optional palette) + pixel data.
    /// To turn it into something the rest of the app can decode, we prepend the 14-byte
    /// BITMAPFILEHEADER (turning it into a complete BMP file in memory), load via System.Drawing,
    /// and re-encode as PNG.
    /// </summary>
    private static byte[]? TryConvertDibToPng(byte[] dibBytes)
    {
        if (dibBytes.Length < 40) return null;

        try
        {
            var infoHeaderSize = BitConverter.ToInt32(dibBytes, 0);
            var bitCount = BitConverter.ToInt16(dibBytes, 14);
            var clrUsed = BitConverter.ToInt32(dibBytes, 32);

            var paletteEntries = bitCount switch
            {
                <= 8 => clrUsed > 0 ? clrUsed : 1 << bitCount,
                _ => 0
            };
            var paletteBytes = paletteEntries * 4;
            var offBits = 14 + infoHeaderSize + paletteBytes;
            var totalSize = 14 + dibBytes.Length;

            using var bmpStream = new MemoryStream(totalSize);
            using (var bw = new BinaryWriter(bmpStream, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write((ushort)0x4D42);   // 'BM'
                bw.Write(totalSize);
                bw.Write((ushort)0);
                bw.Write((ushort)0);
                bw.Write(offBits);
                bw.Write(dibBytes);
            }

            bmpStream.Position = 0;
            using var bitmap = new Bitmap(bmpStream);
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);
            return pngStream.ToArray();
        }
        catch
        {
            // If decoding fails for any reason (corrupt DIB, weird bit depth, OOM), skip ingestion.
            return null;
        }
    }
}
