using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.Notifications;

/// <summary>
/// Shared "build a contextual toast from the pipeline bag" service. Replaces the standalone
/// <c>NotifyToastTask</c> — each task that wants to surface a result calls
/// <see cref="ShowFromBagAsync"/> at the end of its <c>ExecuteAsync</c>, gated by its own
/// <c>showNotification</c> config flag. The button set is derived from the bag exactly the way
/// the old NotifyToastTask did: upload URL wins, otherwise route by <see cref="ItemKind"/>.
/// Image previews inline when both a NewItem(Image) and a local_path are present.
/// </summary>
public sealed class ToastBuilderService : AresToys.Core.Pipeline.IPipelineNotifier
{
    private readonly IToastNotifier _notifier;
    private readonly EditorLauncher _editorLauncher;
    private readonly ILogger<ToastBuilderService> _logger;

    public ToastBuilderService(IToastNotifier notifier, EditorLauncher editorLauncher, ILogger<ToastBuilderService> logger)
    {
        _notifier = notifier;
        _editorLauncher = editorLauncher;
        _logger = logger;
    }

    /// <summary>Show a toast whose body + buttons + inline image are derived from
    /// <paramref name="context"/>. <paramref name="title"/> optionally overrides the "AresToys"
    /// default title. Body is the pipeline's current text (<c>bag.text</c>) — single source of
    /// truth, no template expansion.</summary>
    public void ShowFromBag(PipelineContext context, string? title = null, bool suppressEditorButton = false,
        long? overrideItemId = null, object? overrideItem = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var effectiveTitle = string.IsNullOrEmpty(title) ? "AresToys" : title;
        // Body resolution: bag.text (set by Upload / Save / QR-read / etc.) wins as the canonical
        // status string. When it's empty (typical for an AddTo* step running on a fresh capture
        // with no prior text-producing step), derive a kind-specific status from bag.new_item —
        // "Image added to clipboard — 1920×1080", "File added: arestoys-20260516.png", etc.
        // Hard fallback "Done." stays for the pathological case (no text, no item).
        // Resolve the effective (id, NewItem) the toast should reflect. Override pair wins —
        // terminal Add* tasks pass their freshly-inserted row this way so we can render the
        // right buttons / image preview without polluting bag.item_id (that slot belongs to the
        // workflow's payload-primary item).
        var effectiveItem = overrideItem as NewItem
            ?? (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem ni ? ni : null);
        var effectiveItemId = overrideItemId
            ?? (context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawId) && rawId is long iid ? iid : (long?)null);

        string body;
        if (context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) && rawText is string text && !string.IsNullOrEmpty(text))
        {
            body = text;
        }
        else
        {
            body = DeriveBodyFromItem(context, effectiveItem) ?? Loc("Toast_Body_Done");
        }

        var buttons = BuildButtons(context, effectiveItemId, effectiveItem);
        if (suppressEditorButton)
        {
            // Save-with-skipIfNotModified path: the user already opened the editor upstream
            // (that's the whole point of skipIfNotModified — it only saves when bag.payload_modified
            // is set by Open-editor-before-upload). A second editor button on the toast would
            // just reopen the freshly-closed editor — drop it. Keeps Copy path / Show in folder.
            var openInEditorLabel = Loc("Toast_OpenInEditor");
            buttons = buttons.Where(b => !string.Equals(b.Label, openInEditorLabel, StringComparison.Ordinal)).ToList();
        }
        var imagePath = ResolveInlineImagePath(context);

        _notifier.Show(effectiveTitle, body, onClick: null, imagePath, buttons);
    }

    /// <summary>Build a kind-specific status line from <c>bag.new_item</c>. Returns null when
    /// the bag doesn't have a NewItem at all (caller falls back to "Done."). Image dimensions
    /// are read from the PNG/JPEG header without decoding the full bitmap; failure to read just
    /// drops the dimensions from the line.</summary>
    // CA1863 (cache a CompositeFormat) suppressed: format strings come from a resx that flips
    // with the user's culture, and toast firings are rare (one per pipeline run, ~seconds apart).
    // Caching adds invalidation complexity that exceeds the savings.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1863",
        Justification = "Resx-sourced templates with culture-flip lifecycle; toast fires too rarely to benefit from CompositeFormat caching.")]
    private string? DeriveBodyFromItem(PipelineContext context, NewItem? overrideItem)
    {
        var item = overrideItem
            ?? (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem bagItem ? bagItem : null);
        if (item is null) return null;

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        switch (item.Kind)
        {
            case ItemKind.Image:
                var bytes = item.Payload.ToArray();
                if (TryReadImageDims(bytes, out var w, out var h))
                    return string.Format(culture, Loc("Toast_Body_ImageAddedDims"), w, h);
                return Loc("Toast_Body_ImageAdded");

            case ItemKind.Video:
                return string.Format(culture, Loc("Toast_Body_VideoAdded"), FormatSize(item.PayloadSize));

            case ItemKind.Files:
                // Prefer local_path (set by SaveToFile / RecordScreen / SaveAs) which is the
                // canonical path the user knows; fall back to BlobRef (what AddFile stores into
                // the item itself). Show only the basename — the toast is a one-liner.
                string? path = null;
                if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawLocal) && rawLocal is string lp && !string.IsNullOrEmpty(lp))
                    path = lp;
                else if (!string.IsNullOrEmpty(item.BlobRef))
                    path = item.BlobRef;
                var basename = string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);
                return string.Format(culture, Loc("Toast_Body_FileAdded"), basename);

            case ItemKind.Text:
                // SearchText is already a length-capped preview (set by AddTextToClipboardTask /
                // similar). When absent (other paths), decode up to 80 chars off the payload.
                var preview = item.SearchText;
                if (string.IsNullOrEmpty(preview))
                {
                    var raw = item.Payload.ToArray();
                    var decoded = raw.Length == 0 ? string.Empty :
                        Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 256)).Trim();
                    preview = decoded.Length > 80 ? decoded[..80] + "…" : decoded;
                }
                return string.Format(culture, Loc("Toast_Body_TextAdded"), preview);

            default:
                return null;
        }
    }

    /// <summary>Read just the pixel dimensions out of an image byte buffer without allocating
    /// the decoded bitmap. Same lazy-decode trick <c>EditorLauncher</c> uses. Returns false on
    /// any malformed input so the caller drops the dimensions from the toast line.</summary>
    private static bool TryReadImageDims(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch { return false; }
    }

    /// <summary>Format a byte count as a human-readable size for the toast body — "1.2 MB",
    /// "456 KB", "789 B". Uses the user's current culture for the decimal separator.</summary>
    private static string FormatSize(long bytes)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (bytes >= 1024L * 1024 * 1024)
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", culture) + " GB";
        if (bytes >= 1024L * 1024)
            return (bytes / (1024.0 * 1024)).ToString("0.0", culture) + " MB";
        if (bytes >= 1024)
            return (bytes / 1024.0).ToString("0", culture) + " KB";
        return bytes.ToString(culture) + " B";
    }

    /// <summary>Pick contextual buttons from the bag. Same routing as the old NotifyToastTask:
    /// upload URL → Open/Copy/Editor; otherwise per-Kind (Image → Editor + path + folder, Text
    /// → Open URL if http(s) / Edit otherwise). New addition: a generic "Edit" button for text
    /// items without a URL — writes the payload to a temp .txt and opens it with the system
    /// editor (whichever app handles .txt by default).</summary>
    private IReadOnlyList<ToastButtonChoice> BuildButtons(PipelineContext context, long? overrideItemId, NewItem? overrideItem)
    {
        var list = new List<ToastButtonChoice>();

        // 1. Upload URL path — open + copy URL; offer "open in editor" if the bag still has the
        //    Image item we uploaded (lets the user roundtrip back to editing after sharing).
        //    Gate on bag.uploader_id (set only by UploadTask) so a non-Upload bag.text isn't
        //    misinterpreted as a URL. The actual URL lives in bag.text — legacy bag.upload_url
        //    was retired in 0.1.17 (only ever held a duplicate of bag.text).
        if (context.Bag.ContainsKey(PipelineBagKeys.UploaderId)
            && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawUploadUrl)
            && rawUploadUrl is string uploadUrl && !string.IsNullOrEmpty(uploadUrl))
        {
            list.Add(new ToastButtonChoice(Loc("Toast_OpenUrl"), () => OpenUrlSafe(uploadUrl)));
            list.Add(new ToastButtonChoice(Loc("Toast_CopyUrl"), () => CopyTextSafe(uploadUrl)));
            if (TryGetImageItem(context, overrideItemId, overrideItem, out var imgIdU))
            {
                list.Add(new ToastButtonChoice(Loc("Toast_OpenInEditor"), () => OpenInEditorSafe(imgIdU)));
            }
            return list;
        }

        // 2. Path-only fallback. SaveToFile is typically chained BEFORE AddToHistory, so when a
        //    Save step fires the toast (showNotification:true) the bag has local_path but not
        //    item_id / new_item yet. Surface Copy-path + Show-in-folder anyway so the user still
        //    gets the file actions. "Open in editor" is skipped here because the editor launcher
        //    needs an itemId.
        var hasLocalPath = context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath)
            && rawPath is string lp && !string.IsNullOrEmpty(lp);
        string? localPath = hasLocalPath ? (string)rawPath! : null;
        if (localPath is null
            && context.Bag.TryGetValue("svg_local_path", out var rawSvg)
            && rawSvg is string svgPath && !string.IsNullOrEmpty(svgPath))
        {
            localPath = svgPath;
            hasLocalPath = true;
        }

        // Override pair wins (terminal Add* tasks pass their just-inserted row this way without
        // claiming the bag's item_id slot). Fallback to whatever the bag carries — that's the
        // payload-primary item committed by AddToHistory / capture-task / recording.
        long? itemIdMaybe = overrideItemId
            ?? (context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawId) && rawId is long iid ? iid : (long?)null);
        NewItem? newItemMaybe = overrideItem
            ?? (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawNew) && rawNew is NewItem nit ? nit : null);

        if (itemIdMaybe is null || newItemMaybe is null)
        {
            if (hasLocalPath)
            {
                // Surface "Open in editor" when the saved file is an image — pipeline-typical case
                // is Save-as-Image-file BEFORE Add-to-history (the common default profile order),
                // so item_id isn't in the bag yet but local_path is. Path-driven editor open reads
                // the bytes from disk, runs EditAsync (no history round-trip needed), and writes
                // any edits back to the same file on save. Non-image extensions skip it because
                // the annotation editor only handles rasters.
                var ext = Path.GetExtension(localPath!).TrimStart('.').ToLowerInvariant();
                var isImage = ext is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff";
                if (isImage)
                {
                    list.Add(new ToastButtonChoice(Loc("Toast_OpenInEditor"), () => OpenPathInEditorSafe(localPath!)));
                }
                list.Add(new ToastButtonChoice(Loc("Toast_CopyPathToClipboard"), () => CopyTextSafe(localPath!)));
                list.Add(new ToastButtonChoice(Loc("Toast_ShowInFolder"), () => ShowInFolderSafe(localPath!)));
            }
            return list;
        }

        // 3. Item-driven routing — full set of buttons including "Open in editor".
        var itemId = itemIdMaybe.Value;
        var newItem = newItemMaybe;

        switch (newItem.Kind)
        {
            case ItemKind.Image:
                list.Add(new ToastButtonChoice(Loc("Toast_OpenInEditor"), () => OpenInEditorSafe(itemId)));
                if (hasLocalPath)
                {
                    list.Add(new ToastButtonChoice(Loc("Toast_CopyPathToClipboard"), () => CopyTextSafe(localPath!)));
                    list.Add(new ToastButtonChoice(Loc("Toast_ShowInFolder"), () => ShowInFolderSafe(localPath!)));
                }
                break;

            case ItemKind.Video:
            case ItemKind.Files:
                // Files / recordings: BlobRef OR localPath points at the file. Copy path + Show
                // in folder are the universal actions; no in-app editor for these kinds.
                var filePath = localPath ?? newItem.BlobRef;
                if (!string.IsNullOrEmpty(filePath))
                {
                    list.Add(new ToastButtonChoice(Loc("Toast_CopyPathToClipboard"), () => CopyTextSafe(filePath)));
                    list.Add(new ToastButtonChoice(Loc("Toast_ShowInFolder"), () => ShowInFolderSafe(filePath)));
                }
                break;

            case ItemKind.Text:
                // If the text parses as http(s) → Open URL + Copy. Otherwise → Copy + Edit
                // (system editor via temp .txt). Either way the text is already in the bag,
                // not necessarily on the OS clipboard, so Copy is useful as a distinct action.
                var textBytes = newItem.Payload.ToArray();
                var maybeText = textBytes.Length > 0 && textBytes.Length < 1_048_576
                    ? Encoding.UTF8.GetString(textBytes).Trim()
                    : string.Empty;
                if (Uri.TryCreate(maybeText, UriKind.Absolute, out var parsed)
                    && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    list.Add(new ToastButtonChoice(Loc("Toast_OpenUrl"), () => OpenUrlSafe(parsed.AbsoluteUri)));
                    list.Add(new ToastButtonChoice(Loc("Toast_CopyUrl"), () => CopyTextSafe(parsed.AbsoluteUri)));
                }
                else if (!string.IsNullOrEmpty(maybeText))
                {
                    list.Add(new ToastButtonChoice(Loc("Toast_CopyText"), () => CopyTextSafe(maybeText)));
                    list.Add(new ToastButtonChoice(Loc("Toast_EditText"), () => EditTextSafe(maybeText)));
                }
                break;

            default:
                break;
        }
        return list;
    }

    private static string? ResolveInlineImagePath(PipelineContext context)
    {
        // Inline image preview in the toast template — only when the staged item is an image
        // AND a SaveToFile step put it on disk earlier. Falls back to null = text-only template.
        if (!context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem)) return null;
        if (rawItem is not NewItem item || item.Kind != ItemKind.Image) return null;
        if (!context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath)) return null;
        if (rawPath is not string path || string.IsNullOrEmpty(path)) return null;
        return path;
    }

    private static bool TryGetImageItem(PipelineContext context, long? overrideItemId, NewItem? overrideItem, out long itemId)
    {
        itemId = 0;
        var id = overrideItemId
            ?? (context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawId) && rawId is long i ? i : (long?)null);
        if (id is null) return false;
        var item = overrideItem
            ?? (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawNew) && rawNew is NewItem ni ? ni : null);
        if (item is null) return false;
        if (item.Kind != ItemKind.Image) return false;
        itemId = id.Value;
        return true;
    }

    private void OpenUrlSafe(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Toast button → browser open failed for {Url}", url); }
    }

    private void CopyTextSafe(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { _logger.LogError(ex, "Toast button → clipboard copy failed"); }
    }

    private void OpenInEditorSafe(long itemId)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await _editorLauncher.OpenAsync(itemId, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogError(ex, "Toast button → editor open failed for item {Id}", itemId); }
        });
    }

    /// <summary>Open the annotation editor on a file path. Used by the Save-toast path where no
    /// history item exists yet (Save runs before AddToHistory in default profiles). Delegates to
    /// <see cref="EditorLauncher.EditPathAsync"/> which (a) overwrites the file on save and (b)
    /// also commits the edited bytes to the AresToys clipboard — so the user's modifications
    /// don't evaporate the moment the editor closes. Cancel leaves both file and history
    /// untouched.</summary>
    private void OpenPathInEditorSafe(string path)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await _editorLauncher.EditPathAsync(path, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogError(ex, "Toast button → open path in editor failed for {Path}", path); }
        });
    }

    private void ShowInFolderSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = parent,
                    UseShellExecute = true,
                });
                return;
            }
            _logger.LogInformation("Toast button → file gone for {Path}, parent missing too", path);
        }
        catch (Exception ex) { _logger.LogError(ex, "Toast button → show in folder failed for {Path}", path); }
    }

    /// <summary>Open arbitrary text in the system's default .txt handler (Notepad / VSCode /
    /// whatever the user wired up). Writes a temp file under %TEMP%; the OS cleans those up
    /// eventually so we don't bother. Best-effort: silent on failure.</summary>
    private void EditTextSafe(string text)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var tmp = Path.Combine(Path.GetTempPath(), $"arestoys-text-{stamp}.txt");
            File.WriteAllText(tmp, text, Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tmp,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Toast button → edit text failed"); }
    }

    private static string Loc(string key)
    {
        var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        return Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
    }

}
