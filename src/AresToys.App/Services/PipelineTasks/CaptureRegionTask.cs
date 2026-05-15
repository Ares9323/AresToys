using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;
using AresToys.Capture;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

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
    /// 400 ms is short enough that a deliberate double-tap from the user can't realistically
    /// hit it — nobody actually triggers a second region pick in under half a second.</summary>
    private static readonly TimeSpan OverlayCooldown = TimeSpan.FromMilliseconds(400);

    /// <summary>UTC timestamp of the most recent overlay open. Static so the cooldown is
    /// process-wide (every workflow pipeline shares the same hardware hotkey hook source).</summary>
    private static DateTime _lastOverlayOpenAt = DateTime.MinValue;

    private readonly ICaptureSource _captureSource;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly ILogger<CaptureRegionTask> _logger;

    public CaptureRegionTask(ICaptureSource captureSource, CaptureImageOutputService outputEncoder, ILogger<CaptureRegionTask> logger)
    {
        _captureSource = captureSource;
        _outputEncoder = outputEncoder;
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

        // Snapshot synchronously BEFORE the dispatcher hop — by the time the overlay window
        // is constructed, focus has shifted to AresToys and transient UI like open dropdowns
        // are gone. ShareX-style: capture once at the earliest entry point, hand the bitmap
        // to the overlay, crop on mouse-up.
        var (prefabSnapshot, prefabLeft, prefabTop) = RegionOverlayWindow.CaptureVirtualScreen();
        var (region, prefabBytes) = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var overlay = new RegionOverlayWindow(prefabSnapshot, prefabLeft, prefabTop)
            {
                AutoConfirmOnFirstSelection = autoConfirm,
            };
            var picked = overlay.PickRegion();
            return (picked, overlay.PickedSnapshotBytes);
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
        var (bytes, ext) = await _outputEncoder.EncodeAsync(rawPng, cancellationToken).ConfigureAwait(false);

        context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        context.Bag[PipelineBagKeys.FileExtension] = ext;
        // Stash the on-screen origin in physical pixels so a later pin-to-screen step in the same
        // workflow can place the pinned window exactly where the capture came from. Without this
        // the pin step only sees bytes and centres on the active monitor.
        context.Bag[PipelineBagKeys.CaptureScreenPos] = (region.X, region.Y);
        _logger.LogInformation("Capture region: stored screen pos ({X}, {Y}) {W}×{H} px in bag",
            region.X, region.Y, region.Width, region.Height);
        if (!string.IsNullOrEmpty(region.WindowTitle))
        {
            context.Bag[PipelineBagKeys.WindowTitle] = region.WindowTitle;
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
