using System.Globalization;
using AresToys.Capture;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>The most recently picked capture region, persisted in the settings table as
/// "X,Y,W,H" (physical pixels). Written by every region pick (tray entry-point and the
/// capture-region workflow step), read by the tray "Last region" entry and by capture-region
/// steps configured with <c>useLastRegion</c>.</summary>
internal static class LastCaptureRegion
{
    public const string Key = "capture.last_region";

    public static async Task SaveAsync(ISettingsStore settings, CaptureRegion region, CancellationToken cancellationToken)
    {
        var serialized = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
            region.X, region.Y, region.Width, region.Height);
        await settings.SetAsync(Key, serialized, sensitive: false, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CaptureRegion?> LoadAsync(ISettingsStore settings, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(Key, cancellationToken).ConfigureAwait(false);
        return TryParse(raw, out var region) ? region : null;
    }

    public static bool TryParse(string? raw, out CaptureRegion? region)
    {
        region = null;
        if (string.IsNullOrEmpty(raw)) return false;
        var parts = raw.Split(',');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (w <= 0 || h <= 0) return false;
        region = new CaptureRegion(x, y, w, h, "Last region");
        return true;
    }
}
