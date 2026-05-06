using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using ShareQ.App.Views;
using ShareQ.Capture;
using ShareQ.Core.Domain;
using ShareQ.Core.Pipeline;
using ShareQ.Storage.Items;

namespace ShareQ.App.Services.PipelineTasks;

/// <summary>
/// First step of capture-style workflows: opens the region overlay, captures the selected pixels
/// to a PNG and populates the bag (<c>payload_bytes</c>, <c>file_extension</c>, <c>new_item</c>) so
/// subsequent steps (save, history, upload, …) operate on the captured image.
/// Cancelling the overlay aborts the pipeline via <see cref="PipelineContext.Abort"/>.
/// </summary>
public sealed class CaptureRegionTask : IPipelineTask
{
    public const string TaskId = "shareq.capture-region";

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

        // Snapshot synchronously BEFORE the dispatcher hop — by the time the overlay window
        // is constructed, focus has shifted to ShareQ and transient UI like open dropdowns
        // are gone. ShareX-style: capture once at the earliest entry point, hand the bitmap
        // to the overlay, crop on mouse-up.
        var (prefabSnapshot, prefabLeft, prefabTop) = RegionOverlayWindow.CaptureVirtualScreen();
        var (region, prefabBytes) = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var overlay = new RegionOverlayWindow(prefabSnapshot, prefabLeft, prefabTop);
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
