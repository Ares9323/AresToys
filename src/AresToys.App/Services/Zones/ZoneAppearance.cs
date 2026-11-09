using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Zones;

/// <summary>Look of the zone overlay shown while dragging a window, shared with the layout editor.
/// Opacity values are percents (0 to 100) and apply to the fill colors; the border color keeps its
/// own alpha. <see cref="BorderThickness"/> and <see cref="CornerRadius"/> are in device independent
/// units, so they scale with the monitor DPI.</summary>
public sealed record ZoneAppearance(
    Color Fill,
    int FillOpacityPercent,
    Color HighlightFill,
    int HighlightOpacityPercent,
    Color Border,
    int BorderThickness,
    int CornerRadius)
{
    public const string KeyZoneColor = "zones.appearance.zone_color";
    public const string KeyHighlightColor = "zones.appearance.highlight_color";
    public const string KeyBorderColor = "zones.appearance.border_color";
    public const string KeyZoneOpacity = "zones.appearance.zone_opacity";
    public const string KeyHighlightOpacity = "zones.appearance.highlight_opacity";
    public const string KeyBorderThickness = "zones.appearance.border_thickness";
    public const string KeyCornerRadius = "zones.appearance.corner_radius";

    private static readonly Color Accent = Color.FromRgb(0x93, 0x23, 0xAE);

    public static readonly ZoneAppearance Default = new(Accent, 12, Accent, 40, Accent, 2, 6);

    /// <summary>Reads the appearance keys. A missing, unparsable or out of range value falls back to
    /// the matching default, so a damaged setting never breaks the overlay.</summary>
    public static async Task<ZoneAppearance> LoadAsync(ISettingsStore store, CancellationToken ct)
    {
        var fill = await store.GetAsync(KeyZoneColor, ct).ConfigureAwait(false);
        var highlight = await store.GetAsync(KeyHighlightColor, ct).ConfigureAwait(false);
        var border = await store.GetAsync(KeyBorderColor, ct).ConfigureAwait(false);
        var fillOpacity = await store.GetAsync(KeyZoneOpacity, ct).ConfigureAwait(false);
        var highlightOpacity = await store.GetAsync(KeyHighlightOpacity, ct).ConfigureAwait(false);
        var thickness = await store.GetAsync(KeyBorderThickness, ct).ConfigureAwait(false);
        var radius = await store.GetAsync(KeyCornerRadius, ct).ConfigureAwait(false);

        return new ZoneAppearance(
            ParseColor(fill, Default.Fill),
            ParseInt(fillOpacity, Default.FillOpacityPercent, 0, 100),
            ParseColor(highlight, Default.HighlightFill),
            ParseInt(highlightOpacity, Default.HighlightOpacityPercent, 0, 100),
            ParseColor(border, Default.Border),
            ParseInt(thickness, Default.BorderThickness, 0, 32),
            ParseInt(radius, Default.CornerRadius, 0, 64));
    }

    /// <summary>The look to draw for a style: the saved look for <see cref="ZoneAppearanceStyle.Custom"/>; for
    /// <see cref="ZoneAppearanceStyle.WindowsDefault"/> the Windows accent color for the highlight and the border
    /// and a neutral gray for the other zones, keeping the saved opacities, thickness and radius.</summary>
    public ZoneAppearance ForStyle(ZoneAppearanceStyle style)
    {
        if (style != ZoneAppearanceStyle.WindowsDefault) return this;

        var glass = SystemParameters.WindowGlassColor;
        var accent = Color.FromRgb(glass.R, glass.G, glass.B);
        return this with { Fill = Color.FromRgb(0x80, 0x80, 0x80), HighlightFill = accent, Border = accent };
    }

    /// <summary>The fill color with the alpha channel taken from an opacity percent.</summary>
    public static Color WithOpacity(Color color, int opacityPercent)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(opacityPercent, 0, 100) * 255 / 100.0), color.R, color.G, color.B);

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
        return value < min || value > max ? fallback : value;
    }
}
