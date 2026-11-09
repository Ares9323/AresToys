using System.Globalization;
using System.Windows.Media;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.AlwaysOnTop;

/// <summary>Look of the frame drawn around a pinned window. Written by the Always on top
/// settings tab and re-read by <see cref="AlwaysOnTopService"/> on every pin and on
/// <see cref="AlwaysOnTopService.ReloadSettingsAsync"/>. <c>CornerRadiusPx</c> of -1 means
/// match the window corners automatically.</summary>
internal sealed record AlwaysOnTopSettings(bool ShowFrame, Color Color, int ThicknessPx, int CornerRadiusPx, int OpacityPercent)
{
    public const string KeyShowFrame = "alwaysontop.frame.enabled";
    public const string KeyColor = "alwaysontop.frame.color";
    public const string KeyThickness = "alwaysontop.frame.thickness";
    public const string KeyCornerRadius = "alwaysontop.frame.radius";
    public const string KeyOpacity = "alwaysontop.frame.opacity";

    /// <summary>Module switch (App Settings > Modules). On unless set to "0": the module was always on before the
    /// switch existed.</summary>
    public const string KeyEnabled = "alwaysontop.enabled";

    public static async Task<bool> LoadEnabledAsync(ISettingsStore store, CancellationToken cancellationToken)
        => await store.GetAsync(KeyEnabled, cancellationToken).ConfigureAwait(false) != "0";

    public const string DefaultColorHex = "#9323AE";
    public const int AutoCorner = -1;

    public static readonly AlwaysOnTopSettings Default = new(true, Color.FromRgb(0x93, 0x23, 0xAE), 4, AutoCorner, 100);

    public static async Task<AlwaysOnTopSettings> LoadAsync(ISettingsStore store, CancellationToken cancellationToken)
    {
        var show = await store.GetAsync(KeyShowFrame, cancellationToken).ConfigureAwait(false);
        var color = await store.GetAsync(KeyColor, cancellationToken).ConfigureAwait(false);
        var thickness = await store.GetAsync(KeyThickness, cancellationToken).ConfigureAwait(false);
        var radius = await store.GetAsync(KeyCornerRadius, cancellationToken).ConfigureAwait(false);
        var opacity = await store.GetAsync(KeyOpacity, cancellationToken).ConfigureAwait(false);

        return new AlwaysOnTopSettings(
            show != "0",
            ParseColor(color),
            Clamp(thickness, Default.ThicknessPx, 1, 32),
            Clamp(radius, AutoCorner, AutoCorner, 64),
            Clamp(opacity, Default.OpacityPercent, 10, 100));
    }

    public static Color ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { }
        }

        return Default.Color;
    }

    private static int Clamp(string? raw, int fallback, int min, int max)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
}
