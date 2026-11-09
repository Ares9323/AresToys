namespace AresToys.Core.Imaging;

/// <summary>Output format for screenshots, editor saves, and pipeline file writes. Mirrors
/// ShareX's <c>EImageFormat</c> minus TIFF (no real-world value for screenshots — PNG is
/// equally lossless and universally supported, TIFF only matters for niche print/medical
/// workflows that AresToys doesn't target). The string value of each member doubles as the
/// canonical file extension (no dot) and as the JSON config key in pipeline step settings.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Gif,
}

public static class ImageFormatExtensions
{
    /// <summary>Lowercase extension (no dot) — used both as the file suffix and as the bag's
    /// <c>FileExtension</c> value so downstream consumers (uploaders, history, MIME picker)
    /// see a consistent identifier. JPEG canonicalises to "jpg" because that's the common
    /// extension on disk; the format enum stays JPEG for the IANA name.</summary>
    public static string ToExtension(this ImageFormat format) => format switch
    {
        ImageFormat.Png  => "png",
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Bmp  => "bmp",
        ImageFormat.Gif  => "gif",
        _                => "png",
    };

    /// <summary>Tolerant parse for user-supplied or stored strings — accepts both the enum
    /// name and the file extension form ("Jpeg" or "jpg" both yield <see cref="ImageFormat.Jpeg"/>).
    /// Returns null when the input doesn't match any known format, leaving the caller free to
    /// fall back to PNG / preserve original / surface an error.</summary>
    public static ImageFormat? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "png"          => ImageFormat.Png,
            "jpg" or "jpeg" => ImageFormat.Jpeg,
            "bmp"          => ImageFormat.Bmp,
            "gif"          => ImageFormat.Gif,
            _              => null,
        };
    }
}
