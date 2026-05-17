using System.Collections.Specialized;
using System.IO;
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
/// Adds the file just produced by the pipeline (resolved from <c>bag.local_path</c>, set by
/// SaveToFile / RecordScreen / SaveAs / SaveSvg) to AresToys clipboard history as a
/// <see cref="ItemKind.Files"/> entry. Zero config: no template, no fields. Toggle
/// <c>alsoCopyToWindows</c> additionally publishes <c>CF_HDROP</c> so Ctrl+V in Explorer /
/// Telegram / Discord pastes the actual file (paste-as-file).
/// </summary>
public sealed class AddFileToClipboardTask : IPipelineTask
{
    public const string TaskId = "arestoys.add-file";

    private readonly IItemStore _items;
    private readonly IClipboardListener? _listener;
    private readonly ToastBuilderService? _toast;
    private readonly ILogger<AddFileToClipboardTask> _logger;

    public AddFileToClipboardTask(IItemStore items, ILogger<AddFileToClipboardTask> logger, IClipboardListener? listener = null, ToastBuilderService? toast = null)
    {
        _items = items;
        _logger = logger;
        _listener = listener;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Add file path to AresToys clipboard";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alsoCopy = (bool?)config?["alsoCopyToWindows"] ?? false;

        // Resolve from bag.local_path first (canonical for SaveToFile / RecordScreen / SaveAs)
        // then fall back to bag.svg_local_path so Save-as-SVG workflows work the same way.
        string? path = null;
        if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawLocal) && rawLocal is string lp && !string.IsNullOrEmpty(lp))
            path = lp;
        else if (context.Bag.TryGetValue("svg_local_path", out var rawSvg) && rawSvg is string sp && !string.IsNullOrEmpty(sp))
            path = sp;

        if (path is null || !File.Exists(path))
        {
            _logger.LogDebug("AddFileToClipboardTask: bag.local_path / svg_local_path missing or file not on disk — skipping.");
            return;
        }

        // Stage as a Files item. Payload mirrors what ClipboardIngestionService produces from a
        // CF_HDROP event (UTF-8 newline-joined path list) so the popup + paste round-trip identically.
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var newItem = new NewItem(
            Kind: ItemKind.Files,
            Source: ItemSource.Pipeline,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: pathBytes,
            PayloadSize: pathBytes.LongLength,
            BlobRef: path,
            SearchText: path);
        var id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        // Terminal task: do NOT write bag.item_id / bag.new_item — see AddTextToClipboardTask for
        // the rationale. The toast gets the row via the override path below.

        if (alsoCopy)
        {
            var win = new StringCollection { path };
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    _listener?.SuppressNext();
                    System.Windows.Clipboard.SetFileDropList(win);
                }
                catch (Exception ex) { _logger.LogError(ex, "AddFileToClipboardTask: SetFileDropList failed"); }
            });
        }

        if ((bool?)config?["showNotification"] == true && _toast is not null)
        {
            _toast.ShowFromBag(context, (string?)config?["notificationTitle"], overrideItemId: id, overrideItem: newItem);
        }
    }
}
