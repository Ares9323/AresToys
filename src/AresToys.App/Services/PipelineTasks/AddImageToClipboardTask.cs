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
/// Adds the image staged in <c>bag.payload_bytes</c> to AresToys clipboard history as an
/// <see cref="ItemKind.Image"/> entry. Toggle <c>alsoCopyToWindows</c> additionally publishes
/// the PNG onto the Windows clipboard (via <see cref="ClipboardImagePublisher.SetPng"/>, which
/// preserves alpha for paste in Telegram / Discord / browsers).
/// </summary>
public sealed class AddImageToClipboardTask : IPipelineTask
{
    public const string TaskId = "arestoys.add-image";

    private readonly IItemStore _items;
    private readonly IClipboardListener? _listener;
    private readonly ToastBuilderService? _toast;
    private readonly ILogger<AddImageToClipboardTask> _logger;

    public AddImageToClipboardTask(IItemStore items, ILogger<AddImageToClipboardTask> logger, IClipboardListener? listener = null, ToastBuilderService? toast = null)
    {
        _items = items;
        _logger = logger;
        _listener = listener;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Add image to AresToys clipboard";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alsoCopy = (bool?)config?["alsoCopyToWindows"] ?? false;

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] bytes || bytes.Length == 0)
        {
            _logger.LogDebug("AddImageToClipboardTask: bag.payload_bytes missing/empty — skipping.");
            return;
        }

        var newItem = new NewItem(
            Kind: ItemKind.Image,
            Source: ItemSource.Pipeline,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: "Image");
        var id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        // Terminal task: do NOT write bag.item_id / bag.new_item — see AddTextToClipboardTask for
        // the rationale. The toast gets the row via the override path below.

        if (alsoCopy)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    _listener?.SuppressNext();
                    // PNG-aware publish (alpha-preserving) matches AutoPaster's image path.
                    ClipboardImagePublisher.SetPng(bytes);
                }
                catch (Exception ex) { _logger.LogError(ex, "AddImageToClipboardTask: SetPng failed"); }
            });
        }

        if ((bool?)config?["showNotification"] == true && _toast is not null)
        {
            _toast.ShowFromBag(context, (string?)config?["notificationTitle"], overrideItemId: id, overrideItem: newItem);
        }
    }
}
