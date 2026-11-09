using System.Globalization;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Pins;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Peek;

/// <summary>What the Peek window needs about its size: the bounds it had when it was last hidden (physical pixels on
/// the virtual screen, null until it has been used) and whether it should size itself to the image it shows.</summary>
public sealed record PeekWindowSettings(PixelRect? Bounds, bool FitToContent, PeekBrowseMode Browse = PeekBrowseMode.All, Action<PeekBrowseMode>? SaveBrowse = null,
    PeekZoomMode Zoom = PeekZoomMode.Auto, Action<PeekZoomMode>? SaveZoom = null,
    bool Strip = true, Action<bool>? SaveStrip = null)
{
    public static PeekWindowSettings Default { get; } = new(null, false);
}

/// <summary>Reads and writes the Peek window settings: <see cref="BoundsKey"/> ("x,y,w,h" in physical pixels,
/// invariant culture) and <see cref="FitKey"/> ("1" or "0", off by default). The only writer of both keys. The
/// satellite saves the bounds (it owns the window) and reads both at each Peek; the main app saves the fit option from
/// the Peek settings. Nothing is cached.</summary>
public sealed class PeekWindowStore(ISettingsStore settings, ILogger<PeekWindowStore>? logger = null)
{
    public const string BoundsKey = "peek.window.bounds";
    public const string FitKey = "peek.fit_to_content";

    /// <summary>The browse filter ("all", "family" or "extension"; see <see cref="PeekBrowse"/>), all when missing.</summary>
    public const string BrowseKey = "peek.browse";

    /// <summary>The image zoom mode (see <see cref="PeekZoom"/>), auto when missing.</summary>
    public const string ZoomKey = "peek.zoom";

    /// <summary>Whether the thumbnail strip shows ("1" or "0"), on when missing.</summary>
    public const string StripKey = "peek.strip";

    /// <summary>The stored settings; a missing or invalid value gives its default (invalid data is logged).</summary>
    public async Task<PeekWindowSettings> LoadAsync(CancellationToken ct = default)
    {
        var bounds = ParseBounds(await ReadAsync(BoundsKey, ct).ConfigureAwait(false));
        var fit = await ReadAsync(FitKey, ct).ConfigureAwait(false) == "1";
        var browse = PeekBrowse.Parse(await ReadAsync(BrowseKey, ct).ConfigureAwait(false));
        var zoom = PeekZoom.Parse(await ReadAsync(ZoomKey, ct).ConfigureAwait(false));
        var strip = await ReadAsync(StripKey, ct).ConfigureAwait(false) != "0";
        return new PeekWindowSettings(bounds, fit, browse, mode => _ = SaveBrowseQuietlyAsync(mode),
            zoom, mode => _ = SaveZoomQuietlyAsync(mode), strip, on => _ = SaveStripQuietlyAsync(on));
    }

    public Task SaveStripAsync(bool on, CancellationToken ct = default)
        => settings.SetAsync(StripKey, on ? "1" : "0", sensitive: false, ct);

    private async Task SaveStripQuietlyAsync(bool on)
    {
        try
        {
            await SaveStripAsync(on).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Peek window: saving the thumbnail strip choice failed");
        }
    }

    public Task SaveZoomAsync(PeekZoomMode mode, CancellationToken ct = default)
        => settings.SetAsync(ZoomKey, PeekZoom.Format(mode), sensitive: false, ct);

    private async Task SaveZoomQuietlyAsync(PeekZoomMode mode)
    {
        try
        {
            await SaveZoomAsync(mode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Peek window: saving the zoom mode failed");
        }
    }

    public Task SaveBrowseAsync(PeekBrowseMode mode, CancellationToken ct = default)
        => settings.SetAsync(BrowseKey, PeekBrowse.Format(mode), sensitive: false, ct);

    /// <summary>The save the window triggers from its footer menu: a failure is logged, the choice still holds until
    /// the window closes.</summary>
    private async Task SaveBrowseQuietlyAsync(PeekBrowseMode mode)
    {
        try
        {
            await SaveBrowseAsync(mode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Peek window: saving the browse mode failed");
        }
    }

    public Task SaveBoundsAsync(PixelRect bounds, CancellationToken ct = default)
        => settings.SetAsync(BoundsKey, FormatBounds(bounds), sensitive: false, ct);

    public Task SaveFitAsync(bool fit, CancellationToken ct = default)
        => settings.SetAsync(FitKey, fit ? "1" : "0", sensitive: false, ct);

    /// <summary>The text of a rectangle, e.g. <c>100,80,960,640</c>.</summary>
    internal static string FormatBounds(PixelRect bounds)
        => string.Create(CultureInfo.InvariantCulture, $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");

    /// <summary>The rectangle in <paramref name="raw"/>, or null when it is empty, malformed or has no area.</summary>
    internal PixelRect? ParseBounds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0)
        {
            return new PixelRect(x, y, w, h);
        }

        logger?.LogWarning("Peek window: {Key} holds \"{Value}\", which is not a rectangle, ignoring it", BoundsKey, raw);
        return null;
    }

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Peek window: reading {Key} failed, using the default", key);
            return null;
        }
    }
}
