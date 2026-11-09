using System.Globalization;
using Microsoft.Extensions.Logging;
using AresToys.Core.Imaging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>One-stop shop for "I have raw PNG bytes from a capture; what does the user want
/// in the bag?" Reads the global capture format + JPEG quality + AutoJpeg threshold from
/// settings and applies them in a single place — every capture entry point (RegionCapture,
/// ActiveWindow, ActiveMonitor, WebpageCapture, …) calls into this service so a future
/// format/encoding tweak doesn't need a sweep across the producer set.</summary>
public sealed class CaptureImageOutputService
{
    private const string ImageFormatKey = "capture.image_format";
    private const string JpegQualityKey = "capture.jpeg_quality";
    private const string AutoJpegKey = "capture.auto_jpeg";
    private const string AutoJpegThresholdKbKey = "capture.auto_jpeg_threshold_kb";

    private readonly ISettingsStore _settings;
    private readonly IImageEncoder _encoder;
    private readonly ILogger<CaptureImageOutputService> _logger;

    public CaptureImageOutputService(
        ISettingsStore settings,
        IImageEncoder encoder,
        ILogger<CaptureImageOutputService> logger)
    {
        _settings = settings;
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>Translate canonical PNG bytes into the user's chosen output format and report
    /// the resulting extension. Failures fall back to PNG so a misconfigured setting can't
    /// break the capture path. AutoJpeg only fires when the chosen format is PNG (no point
    /// auto-fallback'ing a user who already explicitly picked JPEG / BMP / GIF).</summary>
    public async Task<(byte[] Bytes, string Extension)> EncodeAsync(byte[] sourcePngBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePngBytes);
        var rawFormat = await _settings.GetAsync(ImageFormatKey, cancellationToken).ConfigureAwait(false);
        var format = ImageFormatExtensions.TryParse(rawFormat) ?? ImageFormat.Png;

        var rawQuality = await _settings.GetAsync(JpegQualityKey, cancellationToken).ConfigureAwait(false);
        var quality = int.TryParse(rawQuality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)
            ? Math.Clamp(q, 1, 100) : 90;

        if (format == ImageFormat.Png)
        {
            var rawAuto = await _settings.GetAsync(AutoJpegKey, cancellationToken).ConfigureAwait(false);
            var autoJpeg = rawAuto is null || (bool.TryParse(rawAuto, out var a) && a);
            if (autoJpeg)
            {
                var rawThreshold = await _settings.GetAsync(AutoJpegThresholdKbKey, cancellationToken).ConfigureAwait(false);
                var thresholdKb = int.TryParse(rawThreshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                    ? Math.Max(64, t) : 2048;
                if (sourcePngBytes.LongLength > (long)thresholdKb * 1024)
                {
                    try
                    {
                        var jpeg = _encoder.Encode(sourcePngBytes, ImageFormat.Jpeg, quality);
                        _logger.LogDebug("AutoJpeg: PNG {PngKb} KB > {ThresholdKb} KB → JPEG {JpegKb} KB",
                            sourcePngBytes.Length / 1024, thresholdKb, jpeg.Length / 1024);
                        return (jpeg, ImageFormat.Jpeg.ToExtension());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AutoJpeg encode failed — keeping original PNG");
                    }
                }
            }
            return (sourcePngBytes, ImageFormat.Png.ToExtension());
        }

        try
        {
            return (_encoder.Encode(sourcePngBytes, format, quality), format.ToExtension());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EncodeForOutput: re-encode to {Format} failed — keeping original PNG", format);
            return (sourcePngBytes, ImageFormat.Png.ToExtension());
        }
    }
}
