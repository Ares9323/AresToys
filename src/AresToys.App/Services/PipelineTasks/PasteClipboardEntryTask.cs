using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Workflow step: paste the N-th clipboard entry from a given category. Mirrors the
/// Ctrl+1..9 quick-paste gesture inside the popup, but driven by a pipeline so a workflow can
/// say "fire a hotkey → paste the 3rd entry of category 'Personal'". Empty / out-of-range
/// selections surface a toast so the user notices the no-op instead of the workflow silently
/// completing on a missing entry.</summary>
public sealed class PasteClipboardEntryTask : IPipelineTask
{
    public const string TaskId = "arestoys.clipboard.paste-entry";

    private readonly IItemStore _items;
    private readonly AutoPaster _paster;
    private readonly TargetWindowTracker _target;
    private readonly IToastNotifier _notifier;
    private readonly ILogger<PasteClipboardEntryTask> _logger;

    public PasteClipboardEntryTask(IItemStore items, AutoPaster paster, TargetWindowTracker target, IToastNotifier notifier, ILogger<PasteClipboardEntryTask> logger)
    {
        _items = items;
        _paster = paster;
        _target = target;
        _notifier = notifier;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Paste clipboard entry";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var category = (string?)config?["category"] ?? string.Empty;
        var entryNumber = (int?)config?["entry"] ?? 1; // 1-based, matches Ctrl+1..9 in the popup

        if (entryNumber < 1)
        {
            _logger.LogWarning("PasteClipboardEntryTask: invalid entry number {Entry}", entryNumber);
            return;
        }

        // Empty category string is allowed — same semantics as the popup's "All" tab: list
        // every item regardless of category and take the N-th most recent.
        var effectiveCategory = string.IsNullOrWhiteSpace(category) ? null : category;
        var query = new ItemQuery(
            Limit: Math.Max(entryNumber, 9),  // pad to 9 so the common Ctrl+1..9 range is covered in one round-trip
            IncludePayload: false,             // payload is fetched again inside AutoPaster.PasteAsync
            Category: effectiveCategory);
        var list = await _items.ListAsync(query, cancellationToken).ConfigureAwait(false);

        if (list.Count < entryNumber)
        {
            var label = effectiveCategory ?? "Clipboard";
            var message = list.Count == 0
                ? $"{label} is empty."
                : $"{label} has only {list.Count} entr{(list.Count == 1 ? "y" : "ies")} — can't paste #{entryNumber}.";
            _notifier.Show("AresToys", message);
            _logger.LogInformation("PasteClipboardEntryTask: short-circuited — category='{Category}' entry={Entry} available={Count}",
                label, entryNumber, list.Count);
            return;
        }

        // Same rationale as PasteHistoryItemTask: workflows triggered via hotkey / tray never
        // open the popup, so TargetWindowTracker._captured stays IntPtr.Zero and AutoPaster's
        // TryRestoreCaptured returns false on the first run. The current foreground window IS
        // the intended paste target at this point (the keyboard hook doesn't steal focus), so
        // capture it here before handing off to AutoPaster.
        _target.CaptureCurrentForeground();

        var target = list[entryNumber - 1];
        await _paster.PasteAsync(target.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("PasteClipboardEntryTask: pasted item {Id} ({Category} #{Entry})",
            target.Id, effectiveCategory ?? "(all)", entryNumber);
    }
}
