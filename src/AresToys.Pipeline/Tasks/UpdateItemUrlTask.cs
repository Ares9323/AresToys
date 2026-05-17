using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.Pipeline.Tasks;

/// <summary>
/// Persists the URL (in <c>bag.text</c>) + uploader id onto the history item identified by
/// <c>bag.item_id</c>. Runs after AddToHistoryTask and an upload step have populated the bag.
/// Gated on <c>bag.uploader_id</c> presence so a non-Upload bag.text (a save path, a decoded
/// QR string, a formatted colour) doesn't get mistakenly committed as the item's URL — only
/// UploadTask sets uploader_id, so its presence is the canonical "did an upload happen" flag.
/// </summary>
public sealed class UpdateItemUrlTask : IPipelineTask
{
    public const string TaskId = "arestoys.update-item-url";

    private readonly IItemStore _items;
    private readonly ILogger<UpdateItemUrlTask> _logger;

    public UpdateItemUrlTask(IItemStore items, ILogger<UpdateItemUrlTask> logger)
    {
        _items = items;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Save uploaded URL on item";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawId) || rawId is not long itemId) return;
        // Gate on uploader_id presence (set by UploadTask). Without an Upload step in the chain
        // bag.text holds whatever the previous text-producing step wrote — typically a saved
        // file path — and committing that as the item's URL would be wrong.
        if (!context.Bag.TryGetValue(PipelineBagKeys.UploaderId, out var rawUploader) || rawUploader is not string uploaderId) return;
        if (!context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawUrl) || rawUrl is not string url || string.IsNullOrEmpty(url)) return;

        var ok = await _items.SetUploadedUrlAsync(itemId, uploaderId, url, cancellationToken).ConfigureAwait(false);
        if (!ok) _logger.LogWarning("UpdateItemUrlTask: SetUploadedUrlAsync({Id}) returned false", itemId);
    }
}
