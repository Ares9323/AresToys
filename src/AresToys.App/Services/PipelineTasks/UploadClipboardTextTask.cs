using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Reads whatever is currently on the Windows clipboard and stages it as the workflow's
/// payload. Detection order — Files → Image → Text — picks the most-specific shape first so a
/// clipboard with both a file drop and a text representation (Explorer's selection ships both)
/// is interpreted as files, not paths-as-text.
/// <para>
/// Bag mapping per detected kind:
/// <list type="bullet">
///   <item><b>Files</b>: <c>bag.local_path</c> = first path, <c>bag.text</c> = first path.
///         Downstream <c>Add file path</c> / <c>Upload (file category)</c> consumes this.</item>
///   <item><b>Image</b>: <c>bag.payload_bytes</c> = PNG-encoded bytes,
///         <c>bag.file_extension</c> = <c>"png"</c>. Downstream image-handling tasks treat it like
///         a fresh capture.</item>
///   <item><b>Text</b>: <c>bag.text</c> = string, <c>bag.payload_bytes</c> = UTF-8 bytes,
///         <c>bag.file_extension</c> = <c>"txt"</c>. Both shapes set so the workflow can chain
///         either text-driven (Add text, Shorten URL) or payload-driven (Upload text category)
///         steps without a manual translator.</item>
/// </list>
/// </para>
/// <para>
/// Skips silently when the clipboard is empty — keeps the workflow hotkey-forgiving (mis-fires
/// don't abort the chain; the user retries after copying something). Downstream tasks each
/// validate their own preconditions (Upload skips on missing payload_bytes, AddFilePath skips
/// on missing local_path, etc.) so a clipboard that doesn't match what the rest of the workflow
/// expects produces a quiet no-op instead of a noisy mid-pipeline failure.
/// </para></summary>
public sealed class UploadClipboardTextTask : IPipelineTask
{
    // TaskId kept as-is for backward compat with profiles persisted before the 0.1.17 generalisation
    // (when this task was strictly text-only). Renaming would orphan stored steps; the seeder would
    // then have to migrate them. Keeping the legacy id is cheaper and harmless — the new behaviour
    // is a superset of the old one.
    public const string TaskId = "arestoys.upload-clipboard-text";

    private readonly ILogger<UploadClipboardTextTask> _logger;

    public UploadClipboardTextTask(ILogger<UploadClipboardTextTask> logger) => _logger = logger;

    public string Id => TaskId;
    public string DisplayName => "Read Windows clipboard";
    public PipelineTaskKind Kind => PipelineTaskKind.PostClipboard;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Clipboard APIs must run on the UI / STA thread. We pull everything we need synchronously
        // so the bag is fully populated by the time we return.
        var snapshot = new ClipboardSnapshot();
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var list = System.Windows.Clipboard.GetFileDropList();
                    if (list is { Count: > 0 } && !string.IsNullOrEmpty(list[0]))
                    {
                        snapshot.Path = list[0];
                        return;
                    }
                }
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var bmp = System.Windows.Clipboard.GetImage();
                    if (bmp is not null)
                    {
                        bmp.Freeze();
                        snapshot.Image = bmp;
                        return;
                    }
                }
                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text)) snapshot.Text = text;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "ReadClipboard: clipboard read failed"); }
        });

        if (snapshot.Path is { } path)
        {
            // Files-category payload: stage the path in both local_path AND text so downstream
            // tasks reading either bag slot work without extra translation. Multi-file selections
            // expose just the first entry — keeping the pipeline single-payload simplifies the
            // contract; future "Read clipboard files" variant could surface the whole list.
            context.Bag[PipelineBagKeys.LocalPath] = path;
            context.Bag[PipelineBagKeys.Text] = path;
            _logger.LogDebug("ReadClipboard: staged file path {Path}", path);
            return Task.CompletedTask;
        }

        if (snapshot.Image is { } image)
        {
            var bytes = EncodePng(image);
            context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            context.Bag[PipelineBagKeys.FileExtension] = "png";
            _logger.LogDebug("ReadClipboard: staged image ({Bytes} bytes PNG)", bytes.Length);
            return Task.CompletedTask;
        }

        if (snapshot.Text is { } str)
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            context.Bag[PipelineBagKeys.Text] = str;
            context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            context.Bag[PipelineBagKeys.FileExtension] = "txt";
            _logger.LogDebug("ReadClipboard: staged {Bytes} bytes of text", bytes.Length);
            return Task.CompletedTask;
        }

        _logger.LogInformation("ReadClipboard: clipboard is empty / unsupported kind — bag untouched");
        return Task.CompletedTask;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Three-way clipboard snapshot taken on the UI thread in a single Invoke so the
    /// detection order is deterministic against fast-changing clipboard owners (paste rapidly
    /// from a different source between two pipeline runs). Mutually-exclusive in practice —
    /// only one field is populated per run.</summary>
    private sealed class ClipboardSnapshot
    {
        public string? Path;
        public BitmapSource? Image;
        public string? Text;
    }
}
