using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AresToys.Clipboard.Native;

namespace AresToys.Clipboard;

[SupportedOSPlatform("windows")]
public sealed class Win32ClipboardReader : IClipboardReader
{
    private readonly IForegroundProcessProbe _probe;
    private readonly uint _cfHtml;
    private readonly uint _cfRtf;
    private readonly uint _cfPng;

    public Win32ClipboardReader(IForegroundProcessProbe probe)
    {
        _probe = probe;
        _cfHtml = ClipboardNativeMethods.RegisterClipboardFormat("HTML Format");
        _cfRtf = ClipboardNativeMethods.RegisterClipboardFormat("Rich Text Format");
        _cfPng = ClipboardNativeMethods.RegisterClipboardFormat("PNG");
    }

    public ClipboardChange? ReadCurrent(IntPtr ownerHwnd)
    {
        if (!ClipboardNativeMethods.OpenClipboard(ownerHwnd)) return null;
        try
        {
            var (format, payload, preview, files) = ReadFirstAvailable();
            if (format == ClipboardFormat.None) return null;

            return new ClipboardChange(
                Format: format,
                CapturedAt: DateTimeOffset.UtcNow,
                SourceProcess: _probe.GetForegroundProcessName(),
                SourceWindow: _probe.GetForegroundWindowTitle(),
                Payload: payload,
                PreviewText: preview,
                FilePaths: files);
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }
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
            var bytes = ReadGlobalBytes(_cfHtml);
            // Prefer CF_UNICODETEXT (UTF-16, always clean) for the preview when available — some
            // sources put double-encoded UTF-8 in CF_HTML and our extractor would inherit the
            // mojibake. Fall back to extracting plaintext from the HTML body otherwise.
            var preview = ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText)
                ? TruncatePreview(ReadUnicodeText())
                : ExtractHtmlPreview(bytes);
            return (ClipboardFormat.Html, bytes, preview, null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfRtf))
        {
            var bytes = ReadGlobalBytes(_cfRtf);
            var preview = ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText)
                ? TruncatePreview(ReadUnicodeText())
                : ExtractRtfPreview(bytes);
            return (ClipboardFormat.Rtf, bytes, preview, null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfUnicodeText))
        {
            var text = ReadUnicodeText();
            var bytes = Encoding.UTF8.GetBytes(text);
            return (ClipboardFormat.Text, bytes, text, null);
        }
        // The registered "PNG" format (browsers, image editors, AresToys itself) keeps the alpha
        // channel; CF_DIB goes through a BMP decode that drops it, so a transparent image would
        // come back with a black or white background.
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(_cfPng))
        {
            var pngBytes = ReadGlobalBytes(_cfPng);
            if (IsPng(pngBytes)) return (ClipboardFormat.Image, pngBytes, "[image]", null);
        }
        if (ClipboardNativeMethods.IsClipboardFormatAvailable(ClipboardNativeMethods.CfDib))
        {
            var dibBytes = ReadGlobalBytes(ClipboardNativeMethods.CfDib);
            var pngBytes = TryConvertDibToPng(dibBytes);
            return pngBytes is not null
                ? (ClipboardFormat.Image, pngBytes, "[image]", null)
                : (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
        }
        return (ClipboardFormat.None, ReadOnlyMemory<byte>.Empty, null, null);
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    private static byte[] ReadGlobalBytes(uint format)
    {
        var hMem = ClipboardNativeMethods.GetClipboardData(format);
        if (hMem == IntPtr.Zero) return [];
        var size = (int)ClipboardNativeMethods.GlobalSize(hMem);
        if (size <= 0) return [];
        var ptr = ClipboardNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return [];
        try
        {
            var buffer = new byte[size];
            Marshal.Copy(ptr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(hMem);
        }
    }

    private static string ReadUnicodeText()
    {
        var hMem = ClipboardNativeMethods.GetClipboardData(ClipboardNativeMethods.CfUnicodeText);
        if (hMem == IntPtr.Zero) return string.Empty;
        var ptr = ClipboardNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
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

    private static string ExtractPreview(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 256) text = text[..256] + "…";
        return text;
    }

    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > 256 ? text[..256] + "…" : text;
    }

    /// <summary>Strips CF_HTML preamble + tags so the row preview / FTS index sees readable text
    /// instead of "Version:0.9 StartHTML:..." gibberish.</summary>
    private static string ExtractHtmlPreview(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var text = Encoding.UTF8.GetString(bytes);

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
        var text = Encoding.UTF8.GetString(bytes);
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
