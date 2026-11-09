using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace AresToys.App.Services.Qr;

/// <summary>Single entry point for QR code generation across the app — pipeline tasks, the
/// live-preview generator window, and the clipboard "Generate QR code…" action all funnel
/// through here. QRCoder gives us both a raster (<see cref="PngByteQRCode"/>) and a vector
/// (<see cref="SvgQRCode"/>) renderer; ShareX's ZXing.Net only does raster, so picking
/// QRCoder as the backend was deliberate. Failure paths log + return null/empty rather than
/// throwing, mirroring the rest of our pipeline-task convention.</summary>
public sealed class QrCodeService
{
    private readonly ILogger<QrCodeService> _logger;

    public QrCodeService(ILogger<QrCodeService> logger) { _logger = logger; }

    /// <summary>PNG bytes ready to write to disk or feed into a clipboard / pipeline step.
    /// <paramref name="pixelsPerModule"/> is the side length of one black/white square in the
    /// final image — 10–14 is a sweet spot for screen scanning, ≤6 starts to look pixelated
    /// on Retina-class displays.</summary>
    public byte[]? TryRenderPng(string text, int pixelsPerModule = 12, QRCodeGenerator.ECCLevel level = QRCodeGenerator.ECCLevel.Q)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, level);
            using var png = new PngByteQRCode(data);
            return png.GetGraphic(Math.Max(1, pixelsPerModule));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QrCodeService: PNG render failed for {Len}-char text", text.Length);
            return null;
        }
    }

    /// <summary>SVG string. The vector renderer ignores <paramref name="pixelsPerModule"/>
    /// when sizing because SVG is resolution-independent, but QRCoder still wants a
    /// per-module pixel size for the document's intrinsic <c>viewBox</c> — pass through the
    /// same value as the raster path so the on-disk default size feels comparable.</summary>
    public string? TryRenderSvg(string text, int pixelsPerModule = 12, QRCodeGenerator.ECCLevel level = QRCodeGenerator.ECCLevel.Q)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, level);
            var svg = new SvgQRCode(data);
            return svg.GetGraphic(Math.Max(1, pixelsPerModule));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QrCodeService: SVG render failed for {Len}-char text", text.Length);
            return null;
        }
    }

    /// <summary>WPF-friendly helper used by the live-preview window: decode the PNG into a
    /// frozen <see cref="BitmapSource"/> so the UI thread can bind to it without re-decoding
    /// per frame. Returns null on failure (caller hides the preview).</summary>
    public BitmapSource? TryRenderBitmap(string text, int pixelsPerModule = 12, QRCodeGenerator.ECCLevel level = QRCodeGenerator.ECCLevel.Q)
    {
        var bytes = TryRenderPng(text, pixelsPerModule, level);
        if (bytes is null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QrCodeService: PNG → BitmapSource decode failed");
            return null;
        }
    }
}
