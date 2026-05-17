using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.Pipeline.Tasks;

public sealed class AddToHistoryTask : IPipelineTask
{
    public const string TaskId = "arestoys.add-to-history";

    private readonly IItemStore _items;
    private readonly ILogger<AddToHistoryTask> _logger;
    private readonly IItemClipboardPublisher? _osClipboardPublisher;
    private readonly AresToys.Core.Pipeline.IPipelineNotifier? _notifier;

    public AddToHistoryTask(IItemStore items, ILogger<AddToHistoryTask> logger, IItemClipboardPublisher? osClipboardPublisher = null, AresToys.Core.Pipeline.IPipelineNotifier? notifier = null)
    {
        _items = items;
        _logger = logger;
        // Optional: only the App composition wires up these. Pipeline tests / headless contexts
        // get null and the matching toggles (alsoCopyToWindows, showNotification) become no-ops.
        _osClipboardPublisher = osClipboardPublisher;
        _notifier = notifier;
    }

    public string Id => TaskId;
    public string DisplayName => "Add to history";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alsoCopy = (bool?)config?["alsoCopyToWindows"] ?? false;

        // Resolve the item id: either an earlier task (RecordScreenTask via the coordinator)
        // already added the item and stamped bag.ItemId — in which case we skip the AddAsync
        // step to avoid a duplicate history row — OR we stage the NewItem ourselves.
        long id;
        if (context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var existingIdRaw) && existingIdRaw is long existingId)
        {
            id = existingId;
            _logger.LogDebug("AddToHistoryTask: bag already has ItemId={Id} — skipping AddAsync (earlier task committed the row).", id);
        }
        else
        {
            if (!context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var raw) || raw is not NewItem newItem)
            {
                _logger.LogWarning("AddToHistoryTask: bag key '{Key}' missing or not a NewItem; skipping", PipelineBagKeys.NewItem);
                return;
            }

            // If a previous step (SaveToFileTask) wrote the file to disk, persist that path as
            // BlobRef so the UI's "Show in folder" command can locate the saved file.
            if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath) && rawPath is string path
                && string.IsNullOrEmpty(newItem.BlobRef))
            {
                newItem = newItem with { BlobRef = path };
                context.Bag[PipelineBagKeys.NewItem] = newItem;
            }

            id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
            context.Bag[PipelineBagKeys.ItemId] = id;
            _logger.LogDebug("AddToHistoryTask: stored item {Id} ({Kind})", id, newItem.Kind);
        }

        // Optional companion publish — pushes the item to the Windows clipboard so Ctrl+V (and
        // the OS clipboard history if the user enabled Win+V system-wide) pick it up. Runs
        // independently of the AddAsync path so it works equally well after a RecordScreenTask
        // (which already added the row) or after a regular capture chain. Publisher suppresses
        // the listener's next ingestion so the SetClipboard call doesn't echo back.
        if (alsoCopy)
        {
            if (_osClipboardPublisher is null)
            {
                _logger.LogWarning("AddToHistoryTask: 'alsoCopyToWindows' requested but no IItemClipboardPublisher is wired up — skipping.");
            }
            else
            {
                try { await _osClipboardPublisher.PublishAsync(id, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "AddToHistoryTask: OS clipboard publish failed for item {Id}", id); }
            }
        }

        if ((bool?)config?["showNotification"] == true && _notifier is not null)
        {
            _notifier.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
    }
}
