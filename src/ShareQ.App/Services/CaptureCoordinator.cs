using System.Globalization;
using System.Windows;
using Microsoft.Extensions.Logging;
using ShareQ.App.Views;
using ShareQ.Capture;
using ShareQ.Core.Domain;
using ShareQ.Core.Pipeline;
using ShareQ.Pipeline;
using ShareQ.Pipeline.Profiles;
using ShareQ.Storage.Items;
using ShareQ.Storage.Settings;

namespace ShareQ.App.Services;

public sealed class CaptureCoordinator
{
    private const string LastRegionKey = "capture.last_region";
    private const string DelayKey = "capture.delay_seconds";

    private readonly ICaptureSource _captureSource;
    private readonly PipelineExecutor _executor;
    private readonly IPipelineProfileStore _profiles;
    private readonly ISettingsStore _settings;
    private readonly IServiceProvider _services;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly ILogger<CaptureCoordinator> _logger;

    public CaptureCoordinator(
        ICaptureSource captureSource,
        PipelineExecutor executor,
        IPipelineProfileStore profiles,
        ISettingsStore settings,
        IServiceProvider services,
        CaptureImageOutputService outputEncoder,
        ILogger<CaptureCoordinator> logger)
    {
        _captureSource = captureSource;
        _executor = executor;
        _profiles = profiles;
        _settings = settings;
        _services = services;
        _outputEncoder = outputEncoder;
        _logger = logger;
    }

    public async Task CaptureRegionAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Capture region: opening overlay");
        // Take the screen snapshot RIGHT NOW — before any dispatcher hop, before constructing
        // the overlay window — so transient UI (open dropdowns, hover popups) stays visible
        // in the captured image. Same pattern ShareX uses (see CaptureRegion.cs:76-95). If we
        // wait for the overlay to take its own snapshot, the dropdown is gone by the time we
        // get there because focus has shifted to ShareQ in the meantime.
        var (prefabSnapshot, prefabLeft, prefabTop) = RegionOverlayWindow.CaptureVirtualScreen();
        var (region, prefab) = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var overlay = new RegionOverlayWindow(prefabSnapshot, prefabLeft, prefabTop);
            var picked = overlay.PickRegion();
            return (picked, overlay.PickedSnapshotBytes);
        }).Task.ConfigureAwait(false);

        if (region is null)
        {
            _logger.LogInformation("Capture region: cancelled");
            return;
        }

        _logger.LogInformation("Capture region: picked ({X}, {Y}) {W}×{H} px (prefab snapshot: {Has})",
            region.X, region.Y, region.Width, region.Height, prefab is not null);
        await PersistLastRegionAsync(region, cancellationToken).ConfigureAwait(false);
        // Pass the cropped snapshot bytes from the overlay so we don't BitBlt twice — the
        // overlay already captured the full virtual screen at open-time, and the bytes here
        // are that snapshot cropped to the picked region. Skipping the second capture means
        // transient UI (open dropdowns, hover popups) stays visible exactly as the user saw
        // it — without the gap between mouse-up and the second BitBlt the dropdown would
        // close in.
        await RunPipelineAsync(region, ItemSource.CaptureRegion, cancellationToken, prefab).ConfigureAwait(false);
    }

    public async Task CaptureFullscreenAsync(CancellationToken cancellationToken)
    {
        await ApplyDelayAsync(cancellationToken).ConfigureAwait(false);
        var (left, top, w, h) = VirtualScreen.GetBounds();
        if (w <= 0 || h <= 0) { _logger.LogWarning("Fullscreen: virtual screen has no size"); return; }
        var region = new CaptureRegion(left, top, w, h, "Fullscreen");
        await RunPipelineAsync(region, ItemSource.CaptureFullscreen, cancellationToken).ConfigureAwait(false);
    }

    public async Task CaptureMonitorAsync(MonitorInfo monitor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        await ApplyDelayAsync(cancellationToken).ConfigureAwait(false);
        var region = new CaptureRegion(monitor.X, monitor.Y, monitor.Width, monitor.Height, $"Monitor {monitor.Name}");
        await RunPipelineAsync(region, ItemSource.CaptureMonitor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run the webpage-capture workflow. Doesn't pre-fill any payload — the workflow's
    /// first step opens the URL prompt dialog and renders the page in a hidden WebView2.</summary>
    public async Task CaptureWebpageAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(DefaultPipelineProfiles.WebpageCaptureId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            _logger.LogWarning("webpage-capture profile not found; aborting");
            return;
        }
        var ctx = new PipelineContext(_services);
        await _executor.RunAsync(profile, ctx, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Capture the currently-foreground window as PNG. Reads <c>GetForegroundWindow</c>
    /// after a short tick (so a tray-menu launch has time to dismiss the popup and let the previous
    /// window regain focus) plus the user-configured <c>capture.delay_seconds</c>. Skips own-process
    /// windows so a Settings dialog or the tray menu itself never become the target.</summary>
    public async Task CaptureActiveWindowAsync(CancellationToken cancellationToken)
    {
        // 50ms gives the menu time to close and Windows time to restore the previous foreground;
        // without it the active window briefly is ShareQ itself when launched from the tray.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        await ApplyDelayAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = WindowEnumeration.GetForegroundWindowSnapshot(excludeProcessId: Environment.ProcessId);
        if (snapshot is null)
        {
            _logger.LogInformation("Active window: no eligible foreground window (own process / minimised / cloaked)");
            return;
        }

        _logger.LogInformation("Active window: capturing '{Title}' at ({X}, {Y}) {W}×{H} px",
            snapshot.Title, snapshot.X, snapshot.Y, snapshot.Width, snapshot.Height);
        var region = new CaptureRegion(snapshot.X, snapshot.Y, snapshot.Width, snapshot.Height,
            string.IsNullOrEmpty(snapshot.Title) ? "Active window" : snapshot.Title);
        await RunPipelineAsync(region, ItemSource.CaptureWindow, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDelayAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(DelayKey, cancellationToken).ConfigureAwait(false);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            _logger.LogDebug("Capture: waiting {Seconds}s before capture", seconds);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 30)), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CaptureLastRegionAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAsync(LastRegionKey, cancellationToken).ConfigureAwait(false);
        if (TryParseRegion(stored, out var region))
        {
            await RunPipelineAsync(region!, ItemSource.CaptureRegion, cancellationToken).ConfigureAwait(false);
            return;
        }
        _logger.LogInformation("LastRegion: nothing stored yet — falling back to region picker");
        await CaptureRegionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPipelineAsync(CaptureRegion region, ItemSource source, CancellationToken cancellationToken, byte[]? prefabPng = null)
    {
        // Region capture passes prefabPng = the snapshot cropped at mouse-up time; everything
        // else (fullscreen, monitor, last-region, active-window-via-this-path) BitBlts here.
        // Wrapping the prefab in CapturedImage keeps the rest of the pipeline ignorant.
        var captured = prefabPng is { Length: > 0 }
            ? new CapturedImage(region.Width, region.Height, prefabPng)
            : await _captureSource.CaptureAsync(region, cancellationToken).ConfigureAwait(false);

        var profile = await _profiles.GetAsync(DefaultPipelineProfiles.RegionCaptureId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            _logger.LogWarning("region-capture profile not found; aborting");
            return;
        }

        // Re-encode the captured PNG into the user's preferred format. The capture pipeline
        // always produces PNG internally (DXGI / GDI surface → PNG) so we can decode that as
        // the canonical source; the choice between PNG / JPEG / BMP / GIF is purely an output
        // concern and lives entirely behind CaptureImageOutputService.
        var (bytes, ext) = await _outputEncoder.EncodeAsync(captured.PngBytes, cancellationToken).ConfigureAwait(false);

        var ctx = new PipelineContext(_services);
        ctx.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        ctx.Bag[PipelineBagKeys.FileExtension] = ext;
        if (!string.IsNullOrEmpty(region.WindowTitle))
        {
            ctx.Bag[PipelineBagKeys.WindowTitle] = region.WindowTitle;
        }
        var searchTextPrefix = string.IsNullOrEmpty(region.WindowTitle) ? source.ToString() : region.WindowTitle;
        ctx.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Image,
            Source: source,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: $"{searchTextPrefix} {captured.Width}×{captured.Height}");

        await _executor.RunAsync(profile, ctx, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistLastRegionAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        // Stored as "X,Y,W,H" — small enough to keep in the settings table without serialization.
        var serialized = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
            region.X, region.Y, region.Width, region.Height);
        try { await _settings.SetAsync(LastRegionKey, serialized, sensitive: false, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist last-region bounds"); }
    }


    private static bool TryParseRegion(string? raw, out CaptureRegion? region)
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
