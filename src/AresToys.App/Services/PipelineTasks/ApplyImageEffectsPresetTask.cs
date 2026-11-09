using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.ImageEffects;
using AresToys.Storage.ImageEffects;
using AresToys.Storage.Items;
using SkiaSharp;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Apply a saved <see cref="EffectPreset"/> to whatever PNG bytes the pipeline is
/// currently carrying in <see cref="PipelineBagKeys.PayloadBytes"/>. Matches by
/// <c>preset_id</c> first (stable identifier survives renames), then falls back to
/// <c>preset_name</c> case-insensitively (friendlier for users wiring this up by hand). Empty
/// presets and no-bytes-yet runs are silent no-ops so this step is safe to drop into a
/// workflow that may or may not have an image at this point.</summary>
public sealed class ApplyImageEffectsPresetTask : IPipelineTask
{
    public const string TaskId = "arestoys.apply-image-effects-preset";

    private readonly ILogger<ApplyImageEffectsPresetTask> _logger;
    private readonly IImageEffectPresetStore _store;
    private readonly IItemStore _items;

    public ApplyImageEffectsPresetTask(
        ILogger<ApplyImageEffectsPresetTask> logger,
        IImageEffectPresetStore store,
        IItemStore items)
    {
        _logger = logger;
        _store = store;
        _items = items;
    }

    public string Id => TaskId;
    public string DisplayName => "Apply image effects preset";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] pngBytes)
        {
            _logger.LogDebug("ApplyImageEffectsPresetTask: no PayloadBytes in bag, skipping");
            return;
        }

        var presetId = config?["preset_id"]?.GetValue<string>();
        var presetName = config?["preset_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(presetId) && string.IsNullOrWhiteSpace(presetName))
        {
            _logger.LogDebug("ApplyImageEffectsPresetTask: no preset_id / preset_name configured, skipping");
            return;
        }

        var preset = await ResolvePresetAsync(presetId, presetName, cancellationToken).ConfigureAwait(false);
        if (preset is null)
        {
            _logger.LogWarning("ApplyImageEffectsPresetTask: preset '{Id}' / '{Name}' not found — leaving image unchanged",
                presetId ?? "(none)", presetName ?? "(none)");
            return;
        }
        if (preset.Effects.Count == 0)
        {
            _logger.LogDebug("ApplyImageEffectsPresetTask: preset '{Name}' has no effects, skipping", preset.Name);
            return;
        }

        // Optional: snapshot the original NewItem into the history *before* we mutate the
        // bag. Lets a single workflow with one Add-to-history step still end up with both
        // versions ("Region 640×524" + "Region 640×524 - Contrast") in the clipboard popup.
        // Defaults to false so existing flows (single end-state entry) aren't disturbed.
        //
        // Important: stamp CreatedAt with "now" rather than reusing the capture's timestamp.
        // The capture task stamps NewItem.CreatedAt at capture-overlay time, and a `with { ... }`
        // copy keeps that value — so both records (original + modified) end up with identical
        // created_at and the popup ordering (DESC) becomes implementation-defined. Stamping
        // here puts the original strictly older than the modified record we'll write below.
        var keepOriginal = (bool?)config?["keep_original"] ?? false;
        if (keepOriginal
            && context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawOrig)
            && rawOrig is NewItem originalItem)
        {
            try
            {
                await _items.AddAsync(originalItem with { CreatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("ApplyImageEffectsPresetTask: persisted original ({Bytes} bytes) before applying '{Name}'",
                    originalItem.PayloadSize, preset.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyImageEffectsPresetTask: failed to persist original to history");
            }
        }

        try
        {
            using var inputBitmap = SKBitmap.Decode(pngBytes);
            if (inputBitmap is null)
            {
                _logger.LogWarning("ApplyImageEffectsPresetTask: SKBitmap.Decode returned null on {Bytes} bytes — image format unsupported?", pngBytes.Length);
                return;
            }

            using var output = preset.Apply(inputBitmap);
            using var image = SKImage.FromBitmap(output);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            var processed = ms.ToArray();
            context.Bag[PipelineBagKeys.PayloadBytes] = processed;

            // Refresh NewItem.Payload too — capture tasks (CaptureRegion / Window / Monitor) put a
            // NewItem in the bag with a snapshot of the original bytes, and AddToHistoryTask
            // stores that record. Without this update the history (and therefore the toast-click
            // "open item" path) keeps pointing at the pre-effect image. We also append the
            // preset name to the SearchText so the clipboard popup shows e.g. "Region 640×524
            // - Contrast" instead of just the raw capture title.
            if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem item)
            {
                var combinedSearch = string.IsNullOrEmpty(item.SearchText)
                    ? preset.Name
                    : $"{item.SearchText} - {preset.Name}";
                context.Bag[PipelineBagKeys.NewItem] = item with
                {
                    Payload = processed,
                    PayloadSize = processed.LongLength,
                    SearchText = combinedSearch,
                    // Refresh CreatedAt so the modified record sorts after the original one in
                    // the popup (DESC by created_at). Without this both rows inherit the
                    // capture-time timestamp from CaptureRegionTask and tie-break is up to
                    // SQLite, which on this user's box happened to surface the original first.
                    CreatedAt = DateTimeOffset.UtcNow,
                };
            }

            _logger.LogDebug("ApplyImageEffectsPresetTask: applied '{Name}' ({Count} effect(s)) → {InBytes} → {OutBytes} bytes",
                preset.Name, preset.Effects.Count, pngBytes.Length, processed.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyImageEffectsPresetTask: failed to apply preset '{Name}'", preset.Name);
        }
    }

    private async Task<EffectPreset?> ResolvePresetAsync(string? id, string? name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = await _store.GetAsync(id, ct).ConfigureAwait(false);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            // ListAsync round-trips every preset's effects_json, so we don't pay the cost
            // unless the id lookup missed. Tiny set in practice (handful per user).
            var all = await _store.ListAsync(ct).ConfigureAwait(false);
            return all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }
}
