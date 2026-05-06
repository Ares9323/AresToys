using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using ShareQ.Core.Domain;
using ShareQ.Core.Pipeline;
using ShareQ.Storage.Items;

namespace ShareQ.App.Services.PipelineTasks;

public sealed class NotifyToastTask : IPipelineTask
{
    public const string TaskId = "shareq.notify-toast";

    private readonly IToastNotifier _notifier;
    private readonly EditorLauncher _editorLauncher;
    private readonly ILogger<NotifyToastTask> _logger;

    public NotifyToastTask(IToastNotifier notifier, EditorLauncher editorLauncher, ILogger<NotifyToastTask> logger)
    {
        _notifier = notifier;
        _editorLauncher = editorLauncher;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Notify (toast)";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var title = (string?)config?["title"] ?? "ShareQ";

        // Two templates: one for upload-success ("Link ready: …"), one for everything else
        // ("Done.", but typically replaced by the workflow with "Image copied to clipboard"
        // / "Saved {bag.local_path}" etc.). The pipeline doesn't pick between them — we do,
        // based on whether an upload URL landed in the bag.
        var hasUploadUrl = context.Bag.TryGetValue(PipelineBagKeys.UploadUrl, out var rawUrl)
            && rawUrl is string uploadUrlStr
            && !string.IsNullOrEmpty(uploadUrlStr);

        string message;
        if (hasUploadUrl)
        {
            var uploadTemplate = (string?)config?["uploadMessage"] ?? "Link ready: {bag.upload_url}";
            message = ExpandPlaceholders(uploadTemplate, context);
        }
        else
        {
            var template = (string?)config?["message"] ?? "Done.";
            message = ExpandPlaceholders(template, context);
        }

        // Body-click is disabled by design: the previous "tap on toast does different things
        // depending on bag state" UX was confusing, so we now ALWAYS show explicit buttons and
        // let body-click dismiss the toast without action. Buttons are filtered to only those
        // whose preconditions hold (URL present, file on disk, item still in history, etc.).
        var buttons = BuildButtons(context);

        // Inline image preview — only meaningful when the captured/generated payload is an
        // image AND a SaveToFile step landed it on disk earlier in the chain (so we have an
        // absolute path). For text/URL toasts we leave imagePath null and the notifier shows
        // the standard text-only template.
        string? imagePath = null;
        if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItemForImage)
            && rawItemForImage is NewItem itemForImage
            && itemForImage.Kind == ItemKind.Image
            && context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath)
            && rawPath is string localPath
            && !string.IsNullOrEmpty(localPath))
        {
            imagePath = localPath;
        }

        _notifier.Show(title, message, onClick: null, imagePath, buttons);
        return Task.CompletedTask;
    }

    /// <summary>Pick the right action set for the toast based on what's in the pipeline bag.
    /// The outer logic mirrors the old single-click router: upload URL wins over item handling,
    /// image items get editor + path + folder, text items get URL-open only when the payload
    /// parses as http(s). Each button is filtered against its preconditions so we never show
    /// e.g. "Show in folder" without a real file path or "Open in editor" without an item id.
    /// Labels are resolved through <see cref="Resources.Strings.ResourceManager"/> against the
    /// pinned culture (not the Designer.cs static accessors) so adding a new key only needs the
    /// resx update — the auto-generated Designer.cs is checked in and would lag otherwise.</summary>
    private IReadOnlyList<ToastButtonChoice> BuildButtons(PipelineContext context)
    {
        var list = new List<ToastButtonChoice>();

        // Upload URL: primary action is to open it. Copy is the recovery if the user wanted
        // it on the clipboard. Editor button only if there's a real item to roundtrip back to.
        if (context.Bag.TryGetValue(PipelineBagKeys.UploadUrl, out var rawUploadUrl)
            && rawUploadUrl is string uploadUrl
            && !string.IsNullOrEmpty(uploadUrl))
        {
            list.Add(new ToastButtonChoice(Loc("Toast_OpenUrl"), () => OpenUrlSafe(uploadUrl)));
            list.Add(new ToastButtonChoice(Loc("Toast_CopyUrl"), () => CopyTextSafe(uploadUrl)));
            if (context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawIdU) && rawIdU is long itemIdU
                && context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawNewU) && rawNewU is NewItem newU
                && newU.Kind == ItemKind.Image)
            {
                list.Add(new ToastButtonChoice(Loc("Toast_OpenInEditor"), () => OpenInEditorSafe(itemIdU)));
            }
            return list;
        }

        // No upload URL: route by item kind. Image gets editor + (if saved to disk) path
        // copy + show-in-folder. Text gets URL-open if the payload is an http(s) URL.
        var hasItem = context.Bag.TryGetValue(PipelineBagKeys.ItemId, out var rawId) && rawId is long itemId;
        if (!hasItem) return list;
        var hasNewItem = context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawNew) && rawNew is NewItem newItem;
        if (!hasNewItem) return list;

        var typedItemId = (long)rawId!;
        var typedNewItem = (NewItem)rawNew!;

        if (typedNewItem.Kind == ItemKind.Image)
        {
            list.Add(new ToastButtonChoice(Loc("Toast_OpenInEditor"), () => OpenInEditorSafe(typedItemId)));
            if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawLocal)
                && rawLocal is string localPath
                && !string.IsNullOrEmpty(localPath))
            {
                list.Add(new ToastButtonChoice(Loc("Toast_CopyPathToClipboard"), () => CopyTextSafe(localPath)));
                list.Add(new ToastButtonChoice(Loc("Toast_ShowInFolder"), () => ShowInFolderSafe(localPath)));
            }
        }
        else if (typedNewItem.Kind == ItemKind.Text)
        {
            // Text payload → only "Open URL" if it parses as http(s); plain text gets no
            // buttons because it's already on the clipboard and there's no useful action
            // (no editor for raw strings, no path to copy if it wasn't saved as a file).
            var textBytes = typedNewItem.Payload.ToArray();
            var maybeUrl = textBytes.Length > 0 && textBytes.Length < 4096
                ? System.Text.Encoding.UTF8.GetString(textBytes).Trim()
                : string.Empty;
            if (Uri.TryCreate(maybeUrl, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                list.Add(new ToastButtonChoice(Loc("Toast_OpenUrl"), () => OpenUrlSafe(parsed.AbsoluteUri)));
            }
        }

        return list;
    }

    private static string Loc(string key)
    {
        var culture = ShareQ.App.Markup.LocalizedStrings.Instance.Culture
                      ?? System.Globalization.CultureInfo.CurrentUICulture;
        return Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
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

    private void ShowInFolderSafe(string localPath)
    {
        try
        {
            // Defensive: file may have been deleted/moved since the toast appeared. Fall back
            // to opening the parent folder if it still exists, otherwise log + bail.
            if (System.IO.File.Exists(localPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{localPath}\"",
                    UseShellExecute = true,
                });
                return;
            }
            var parent = System.IO.Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = parent,
                    UseShellExecute = true,
                });
                return;
            }
            _logger.LogInformation("Toast button → file gone for {Path}, parent missing too", localPath);
        }
        catch (Exception ex) { _logger.LogError(ex, "Toast button → show in folder failed for {Path}", localPath); }
    }

    private static string ExpandPlaceholders(string template, PipelineContext context)
    {
        if (!template.Contains("{bag.", StringComparison.Ordinal)) return template;

        var sb = new System.Text.StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{' && template.AsSpan(i).StartsWith("{bag.", StringComparison.Ordinal))
            {
                var end = template.IndexOf('}', i);
                if (end < 0) { sb.Append(template, i, template.Length - i); break; }
                var key = template.Substring(i + 5, end - (i + 5));
                if (context.Bag.TryGetValue(key, out var value)) sb.Append(value?.ToString());
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
