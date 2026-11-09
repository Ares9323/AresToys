using System.Text.Json.Nodes;

namespace AresToys.App.Services.ImageResize;

/// <summary>Output encoding parameters. Quality (1-100) only applies to formats that have one.</summary>
public sealed record ImageEncodeOptions(
    ImageOutputFormat Format = ImageOutputFormat.Keep,
    int Quality = 90,
    bool StripMetadata = false)
{
    /// <summary>Reads the step config keys format, quality and strip_metadata. Missing or
    /// unknown values fall back to the defaults.</summary>
    public static ImageEncodeOptions FromConfig(JsonNode? config)
    {
        var format = ConfigReader.GetString(config, "format")?.Trim().ToLowerInvariant() switch
        {
            "png" => ImageOutputFormat.Png,
            "jpg" or "jpeg" => ImageOutputFormat.Jpeg,
            "webp" => ImageOutputFormat.Webp,
            "bmp" => ImageOutputFormat.Bmp,
            "tiff" or "tif" => ImageOutputFormat.Tiff,
            "gif" => ImageOutputFormat.Gif,
            _ => ImageOutputFormat.Keep,
        };
        var q = ConfigReader.GetDouble(config, "quality");
        var quality = q is { } qv && double.IsFinite(qv) ? Math.Clamp((int)Math.Round(qv), 1, 100) : 90;
        return new ImageEncodeOptions(format, quality, ConfigReader.GetBool(config, "strip_metadata") ?? false);
    }
}
