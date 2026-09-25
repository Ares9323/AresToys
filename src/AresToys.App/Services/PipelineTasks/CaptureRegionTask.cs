using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;
using AresToys.Capture;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// First step of capture-style workflows: opens the region overlay, captures the selected pixels
/// to a PNG and populates the bag (<c>payload_bytes</c>, <c>file_extension</c>, <c>new_item</c>) so
/// subsequent steps (save, history, upload, …) operate on the captured image.
/// Cancelling the overlay aborts the pipeline via <see cref="PipelineContext.Abort"/>.
/// </summary>
public sealed class CaptureRegionTask : IPipelineTask
{
    public const string TaskId = "arestoys.capture-region";

    /// <summary>Cooldown between consecutive region-overlay opens. Filters out the
    /// "first-press double-fire" we see on a cold app: the OS hotkey hook can deliver the
    /// keydown event twice when the AresToys hotkey loop is initialising on the very first
    /// press after launch, which would otherwise spawn the overlay twice (the first instance
    /// covers the desktop with a phantom selection that the user has to Esc through).
    /// 150 ms tightens the previous 400 ms guard now that the hook itself no longer leaks
    /// "held" state on KEYUP-matched keys (Print / Pause): the residual stale-state bug used
    /// to drop alternate presses, which the longer cooldown was masking as "user pressed twice
    /// too fast". 150 ms still catches a genuine intra-burst double-fire (sub-frame doubling
    /// at cold start) but stops penalising a real second user press within half a second.</summary>
    private static readonly TimeSpan OverlayCooldown = TimeSpan.FromMilliseconds(150);

    /// <summary>UTC timestamp of the most recent overlay open. Static so the cooldown is
    /// process-wide (every workflow pipeline shares the same hardware hotkey hook source).</summary>
    private static DateTime _lastOverlayOpenAt = DateTime.MinValue;

    private readonly ICaptureSource _captureSource;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly ISettingsStore _settings;
    private readonly ILogger<CaptureRegionTask> _logger;

    private readonly IToastNotifier? _notifier;

    public CaptureRegionTask(ICaptureSource captureSource, CaptureImageOutputService outputEncoder, ISettingsStore settings, ILogger<CaptureRegionTask> logger, IToastNotifier? notifier = null)
    {
        _notifier = notifier;
        _captureSource = captureSource;
        _outputEncoder = outputEncoder;
        _settings = settings;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Capture region";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // If the entry-point already filled the bag (tray Fullscreen / Monitor / Last region pre-fill
        // payload_bytes before invoking the profile) we skip the overlay so the same workflow can
        // serve both hotkey-driven region picks and pre-captured flows.
        if (context.Bag.ContainsKey(PipelineBagKeys.PayloadBytes))
        {
            _logger.LogDebug("CaptureRegionTask: payload already in bag; skipping overlay");
            return;
        }

        // Cooldown guard against the cold-start double-fire (see OverlayCooldown remarks).
        // When two trigger events arrive within the cooldown we abort the second pipeline run
        // instead of opening a duplicate overlay on top of the first one.
        var now = DateTime.UtcNow;
        if (now - _lastOverlayOpenAt < OverlayCooldown)
        {
            _logger.LogInformation("CaptureRegionTask: suppressing repeat trigger ({Elapsed} ms since last open, cooldown {Cooldown} ms)",
                (int)(now - _lastOverlayOpenAt).TotalMilliseconds, (int)OverlayCooldown.TotalMilliseconds);
            context.Abort("region overlay cooldown");
            return;
        }
        _lastOverlayOpenAt = now;

        // Per-workflow opt-in: when set, the overlay closes on the first valid mouse-up
        // (drag rect or snap-to-window click) without waiting for Enter — single-shot
        // semantics matching the pre-multi-region behaviour, useful for "rapid screenshot"
        // workflows where the user never wants to add a second region. Default true:
        // multi-region is power-user opt-in, single-shot is the common case.
        var autoConfirm = (bool?)config?["autoConfirmOnFirstSelection"] ?? true;

        // Per-workflow opt-in: re-capture the last picked region without opening the overlay.
        // Live BitBlt of the same rectangle, so repeated shots of one area (a progress bar, a
        // chart, a game HUD) are a single keypress. Nothing stored yet → normal overlay pick.
        if ((bool?)config?["useLastRegion"] == true)
        {
            var last = await LastCaptureRegion.LoadAsync(_settings, cancellationToken).ConfigureAwait(false);
            if (last is not null)
            {
                if (_notifier is not null) await _notifier.HideOnScreenPopupsAsync().ConfigureAwait(false);
                var captured = await _captureSource.CaptureAsync(last, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Capture region: reusing last region ({X}, {Y}) {W}×{H} px",
                    last.X, last.Y, last.Width, last.Height);
                await PublishAsync(context, last, captured.PngBytes, multiParts: null, cancellationToken).ConfigureAwait(false);
                return;
            }
            _logger.LogInformation("Capture region: no last region stored yet; opening the overlay");
        }

        // Snapshot synchronously BEFORE the dispatcher hop — by the time the overlay window
        // is constructed, focus has shifted to AresToys and transient UI like open dropdowns
        // are gone. ShareX-style: capture once at the earliest entry point, hand the bitmap
        // to the overlay, crop on mouse-up.
        // Take the previous capture's "saved" toast off the screen first, or it ends up in this shot.
        if (_notifier is not null) await _notifier.HideOnScreenPopupsAsync().ConfigureAwait(false);
        var (prefabSnapshot, prefabLeft, prefabTop) = RegionOverlayWindow.CaptureVirtualScreen();
        var (region, prefabBytes, multiParts) = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var overlay = new RegionOverlayWindow(prefabSnapshot, prefabLeft, prefabTop)
            {
                AutoConfirmOnFirstSelection = autoConfirm,
            };
            var picked = overlay.PickRegion();
            return (picked, overlay.PickedSnapshotBytes, overlay.PickedMultiRegionParts);
        }).Task.ConfigureAwait(false);

        if (region is null)
        {
            _logger.LogDebug("CaptureRegionTask: user cancelled the overlay; aborting pipeline");
            context.Abort("region capture cancelled");
            return;
        }

        // If the overlay produced cropped bytes from the prefab snapshot (the common path),
        // use those directly — skips a redundant BitBlt and keeps any animations/dropdowns
        // visible in the snapshot frozen as the user saw them. Only fall back to the live
        // capture source if the prefab path failed (Win32 capture error at overlay open).
        var rawPng = prefabBytes is { Length: > 0 }
            ? prefabBytes
            : (await _captureSource.CaptureAsync(region, cancellationToken).ConfigureAwait(false)).PngBytes;

        // Remember the pick so "use last region" steps (and the tray "Last region" entry) can
        // repeat it. Best-effort: a settings write failure must not lose the capture.
        try { await LastCaptureRegion.SaveAsync(_settings, region, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist last-region bounds"); }

        await PublishAsync(context, region, rawPng, multiParts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encode the captured PNG to the user's output format and fill the bag for the
    /// downstream steps. Shared by the overlay pick and the last-region shortcut.</summary>
    private async Task PublishAsync(PipelineContext context, CaptureRegion region, byte[] rawPng,
        IReadOnlyList<(int X, int Y, byte[] Png)>? multiParts, CancellationToken cancellationToken)
    {
        var (bytes, ext) = await _outputEncoder.EncodeAsync(rawPng, cancellationToken).ConfigureAwait(false);

        context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        context.Bag[PipelineBagKeys.FileExtension] = ext;
        // Stash the on-screen origin in physical pixels so a later pin-to-screen step in the same
        // workflow can place the pinned window exactly where the capture came from. Without this
        // the pin step only sees bytes and centres on the active monitor.
        context.Bag[PipelineBagKeys.CaptureScreenPos] = (region.X, region.Y);
        // Multi-region commits also publish the per-rect crops so PinToScreenTask can spawn N
        // independent windows. Save/History/Upload continue to consume PayloadBytes (the
        // composite) — split behaviour is opt-in per task, not pipeline-wide.
        if (multiParts is { Count: > 1 })
        {
            context.Bag[PipelineBagKeys.MultiRegionParts] = multiParts;
            _logger.LogInformation("Capture region: committed {Count} rects (publishing parts for downstream split)", multiParts.Count);
        }
        _logger.LogInformation("Capture region: stored screen pos ({X}, {Y}) {W}×{H} px in bag",
            region.X, region.Y, region.Width, region.Height);
        if (!string.IsNullOrEmpty(region.WindowTitle))
        {
            context.Bag[PipelineBagKeys.WindowTitle] = region.WindowTitle;
        }
        // App under the centre of the region (the overlay is closed by now, so the real window
        // is on top again). Feeds the %appName file-name token.
        if (AresToys.Capture.WindowEnumeration.GetProcessNameAt(
                region.X + region.Width / 2, region.Y + region.Height / 2) is { } appName)
        {
            context.Bag[PipelineBagKeys.AppName] = appName;
        }
        var searchTextPrefix = string.IsNullOrEmpty(region.WindowTitle) ? "Region" : region.WindowTitle;
        context.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Image,
            Source: ItemSource.CaptureRegion,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: $"{searchTextPrefix} {region.Width}×{region.Height}");
    }
}
