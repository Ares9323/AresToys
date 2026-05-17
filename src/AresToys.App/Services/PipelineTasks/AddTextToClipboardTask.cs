using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Notifications;
using AresToys.Clipboard;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Adds the pipeline's current text (<c>bag.text</c> — overwritten by every text-producing step:
/// SaveToFile / Upload / QrRead / etc.) to AresToys clipboard history as a <see cref="ItemKind.Text"/>
/// entry. Zero config: no template, no fields. Toggle <c>alsoCopyToWindows</c> additionally
/// pushes the text onto the Windows clipboard so Ctrl+V in any app pastes it.
/// </summary>
public sealed class AddTextToClipboardTask : IPipelineTask
{
    public const string TaskId = "arestoys.add-text";

    private readonly IItemStore _items;
    private readonly IClipboardListener? _listener;
    private readonly ToastBuilderService? _toast;
    private readonly ILogger<AddTextToClipboardTask> _logger;

    public AddTextToClipboardTask(IItemStore items, ILogger<AddTextToClipboardTask> logger, IClipboardListener? listener = null, ToastBuilderService? toast = null)
    {
        _items = items;
        _logger = logger;
        _listener = listener;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Add text to AresToys clipboard";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alsoCopy = (bool?)config?["alsoCopyToWindows"] ?? false;

        if (!context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) || rawText is not string text || string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("AddTextToClipboardTask: bag.text missing/empty — skipping (chain a text-producing task before this one).");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var newItem = new NewItem(
            Kind: ItemKind.Text,
            Source: ItemSource.Pipeline,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: text.Length <= 256 ? text : text[..256] + "…");
        var id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        // Terminal task: Inputs=[Text], Outputs=[]. Do NOT write bag.item_id / bag.new_item — those
        // slots belong to the workflow's payload-primary item (set by capture / AddToHistory /
        // recording). The Text row we just inserted is a side artifact: it lives in the DB and
        // the toast renders it via the override path below, but downstream steps must keep seeing
        // the primary item.

        if (alsoCopy)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    _listener?.SuppressNext();
                    System.Windows.Clipboard.SetText(text);
                }
                catch (Exception ex) { _logger.LogError(ex, "AddTextToClipboardTask: SetText failed"); }
            });
        }

        if ((bool?)config?["showNotification"] == true && _toast is not null)
        {
            // Pass the just-inserted (id, NewItem) as override so the toast routes to the Text
            // kind-specific buttons (Open URL if it's a link / Copy + Edit otherwise) without
            // having to write into bag.new_item — that slot may already hold the primary Image
            // item and clobbering it would break upstream toast routing in chained workflows.
            _toast.ShowFromBag(context, (string?)config?["notificationTitle"], overrideItemId: id, overrideItem: newItem);
        }
    }
}
