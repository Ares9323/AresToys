using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using ZXing;
using ZXing.Common;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace AresToys.App.Services;

/// <summary>
/// Pure-managed QR / barcode decoder. Takes PNG bytes (the standard payload format used by every
/// capture step), decodes via ZXing.Net, and returns the embedded text. We feed ZXing the raw
/// pixel array via <see cref="RGBLuminanceSource"/> so we don't drag in the legacy
/// <c>System.Drawing</c> binding — keeps the dependency graph clean and works equally well on
/// any future non-WPF host.
///
/// Hard-restricted to QR_CODE: the wider barcode scanner format set is noisy on screenshots
/// (false positives on UI text-heavy regions) and outside the typical "I have a QR on my screen,
/// what does it say" use case. Trivial to extend later if EAN / Code-128 / etc. become useful.
/// </summary>
public sealed class QrReaderService
{
    private readonly ILogger<QrReaderService> _logger;

    public QrReaderService(ILogger<QrReaderService> logger)
    {
        _logger = logger;
    }

    /// <summary>Decode the first QR code found in <paramref name="pngBytes"/>. Returns the decoded
    /// text, or <c>null</c> when no QR is found / the image can't be decoded. The reader has
    /// <see cref="DecodingOptions.TryHarder"/> + auto-rotation on so partially-tilted captures
    /// still resolve at the cost of a few extra ms.</summary>
    public string? Decode(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(pngBytes, writable: false);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            // Normalise everything into Bgra32 so RGBLuminanceSource has a single layout to chew on.
            var src = frame.Format == WpfPixelFormats.Bgra32
                ? (BitmapSource)frame
                : new FormatConvertedBitmap(frame, WpfPixelFormats.Bgra32, destinationPalette: null, alphaThreshold: 0);

            var width = src.PixelWidth;
            var height = src.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[height * stride];
            src.CopyPixels(pixels, stride, 0);

            var luminance = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = [BarcodeFormat.QR_CODE],
                    TryHarder = true,
                }
            };
            var result = reader.Decode(luminance);
            if (result is null)
            {
                _logger.LogDebug("QrReaderService: no QR code found in {Bytes}-byte PNG ({W}×{H})", pngBytes.Length, width, height);
                return null;
            }
            _logger.LogInformation("QrReaderService: decoded QR ({Chars} chars) from {W}×{H} image", result.Text?.Length ?? 0, width, height);
            return result.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QrReaderService: decode pipeline failed");
            return null;
        }
    }
}
