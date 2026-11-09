using System.Globalization;
using System.Text.Json.Nodes;

namespace AresToys.App.Services.ImageResize;

public enum ResizeFit { Fit, Fill, Stretch }

public enum ResizeUnit { Pixel, Percent, Inch, Centimeter }

public enum ImageOutputFormat { Keep, Png, Jpeg, Webp, Bmp, Tiff, Gif }

/// <summary>Resize parameters shared by the resize pipeline tasks. Width and Height use
/// <see cref="Unit"/>; 0 means auto.</summary>
public sealed record ImageResizeOptions(
    ResizeFit Fit = ResizeFit.Fit,
    ResizeUnit Unit = ResizeUnit.Pixel,
    double Width = 0,
    double Height = 0,
    bool ShrinkOnly = false,
    bool IgnoreOrientation = true)
{
    /// <summary>Reads the step config keys mode, unit, width, height, shrink_only and
    /// ignore_orientation. Missing or unknown values fall back to the defaults.</summary>
    public static ImageResizeOptions FromConfig(JsonNode? config)
    {
        var fit = ConfigReader.GetString(config, "mode")?.Trim().ToLowerInvariant() switch
        {
            "fill" => ResizeFit.Fill,
            "stretch" => ResizeFit.Stretch,
            _ => ResizeFit.Fit,
        };
        var unit = ConfigReader.GetString(config, "unit")?.Trim().ToLowerInvariant() switch
        {
            "percent" => ResizeUnit.Percent,
            "inch" => ResizeUnit.Inch,
            "cm" => ResizeUnit.Centimeter,
            _ => ResizeUnit.Pixel,
        };
        var w = ConfigReader.GetDouble(config, "width") ?? 0;
        var h = ConfigReader.GetDouble(config, "height") ?? 0;
        return new ImageResizeOptions(
            fit, unit,
            double.IsFinite(w) && w > 0 ? w : 0,
            double.IsFinite(h) && h > 0 ? h : 0,
            ConfigReader.GetBool(config, "shrink_only") ?? false,
            ConfigReader.GetBool(config, "ignore_orientation") ?? true);
    }
}

/// <summary>Lenient JSON config readers: a value of the wrong type yields null, never throws.</summary>
internal static class ConfigReader
{
    public static string? GetString(JsonNode? config, string key)
        => config is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? GetDouble(JsonNode? config, string key)
    {
        if (config is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static bool? GetBool(JsonNode? config, string key)
    {
        if (config is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        return null;
    }
}
