using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.Clipboard;
using AresToys.Core.Domain;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Items;

namespace AresToys.App.Services;

/// <summary>
/// WPF-backed implementation of <see cref="IItemClipboardPublisher"/>. Resolves the item via
/// <see cref="IItemStore"/>, then pushes the right shape onto the Windows clipboard depending on
/// <see cref="ItemKind"/>. Mirrors the format-handling logic in <see cref="AutoPaster"/> minus
/// the foreground-restore + Ctrl+V — "put on clipboard, don't simulate a paste". Suppresses the
/// clipboard listener's next ingestion so the SetClipboard call doesn't echo back as a
/// duplicate item in the history.
/// </summary>
public sealed class WpfItemClipboardPublisher : IItemClipboardPublisher
{
    private readonly IItemStore _items;
    private readonly IClipboardListener? _listener;
    private readonly ILogger<WpfItemClipboardPublisher> _logger;

    public WpfItemClipboardPublisher(IItemStore items, ILogger<WpfItemClipboardPublisher> logger, IClipboardListener? listener = null)
    {
        _items = items;
        _logger = logger;
        // Listener may be absent when the Clipboard module is disabled — we just skip the
        // suppression call in that case (no duplicate to worry about: ingestion is off too).
        _listener = listener;
    }

    public async Task PublishAsync(long itemId, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _logger.LogWarning("WpfItemClipboardPublisher: item {Id} not found.", itemId);
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _listener?.SuppressNext();

                switch (record.Kind)
                {
                    case ItemKind.Text:
                        System.Windows.Clipboard.SetText(Encoding.UTF8.GetString(record.Payload.Span));
                        break;
                    case ItemKind.Html:
                        // HTML preserved as plain on clipboard — matches AutoPaster's HTML path
                        // (modern apps that accept CF_HTML expect a Microsoft-specific header;
                        // we don't construct it here, fall back to the readable text version).
                        var htmlPayload = Encoding.UTF8.GetString(record.Payload.Span);
                        var htmlPlain = ClipboardCleaning.HtmlToPlain(htmlPayload);
                        if (string.IsNullOrEmpty(htmlPlain) && !string.IsNullOrEmpty(record.SearchText))
                            htmlPlain = record.SearchText;
                        System.Windows.Clipboard.SetText(htmlPlain);
                        break;
                    case ItemKind.Rtf:
                        var rtfPayload = Encoding.UTF8.GetString(record.Payload.Span);
                        var rtfPlain = ClipboardCleaning.RtfToPlain(rtfPayload);
                        if (string.IsNullOrEmpty(rtfPlain) && !string.IsNullOrEmpty(record.SearchText))
                            rtfPlain = record.SearchText;
                        System.Windows.Clipboard.SetText(rtfPlain);
                        break;
                    case ItemKind.Image:
                        var pngBytes = record.Payload.ToArray();
                        if (pngBytes.Length == 0) return;
                        ClipboardImagePublisher.SetPng(pngBytes);
                        break;
                    case ItemKind.Video:
                    case ItemKind.Files:
                        // Both kinds: BlobRef points at the file on disk (videos) or the payload
                        // is a newline-joined path list (Files from CF_HDROP). Push CF_HDROP back
                        // so paste-as-file works in Explorer / Telegram / etc.
                        var paths = new StringCollection();
                        if (!string.IsNullOrEmpty(record.BlobRef) && File.Exists(record.BlobRef))
                        {
                            paths.Add(record.BlobRef);
                        }
                        else if (record.Kind == ItemKind.Files)
                        {
                            foreach (var p in Encoding.UTF8.GetString(record.Payload.Span)
                                                  .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                if (File.Exists(p)) paths.Add(p);
                            }
                        }
                        if (paths.Count == 0)
                        {
                            _logger.LogWarning("WpfItemClipboardPublisher: item {Id} ({Kind}) has no resolvable on-disk file.", itemId, record.Kind);
                            return;
                        }
                        System.Windows.Clipboard.SetFileDropList(paths);
                        break;
                    default:
                        _logger.LogDebug("WpfItemClipboardPublisher: item {Id} kind {Kind} not supported — skipping.", itemId, record.Kind);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "WpfItemClipboardPublisher: clipboard publish failed for item {Id}", itemId); }
        });
    }
}
