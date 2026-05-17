using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.PluginContracts;

namespace AresToys.Plugins.Tasks;

/// <summary>
/// Pipeline step that uploads <c>bag.payload_bytes</c>. Two modes:
/// <list type="bullet">
///   <item>Single uploader by id: <c>{"uploader":"onedrive"}</c></item>
///   <item>Category (user's selection): <c>{"category":"image"}</c> — runs every uploader the user
///         selected for that category and concatenates the URLs (one per line) onto the clipboard.</item>
/// </list>
/// On success populates: <c>bag.upload_url</c> = first URL, <c>bag.upload_urls</c> = newline-joined
/// list, <c>bag.uploader_id</c> = first uploader's id.
/// </summary>
public sealed class UploadTask : IPipelineTask
{
    public const string TaskId = "arestoys.upload";

    private readonly IUploaderResolver _resolver;
    private readonly AresToys.Core.Pipeline.IPipelineNotifier? _notifier;
    private readonly ILogger<UploadTask> _logger;

    public UploadTask(IUploaderResolver resolver, ILogger<UploadTask> logger, AresToys.Core.Pipeline.IPipelineNotifier? notifier = null)
    {
        _resolver = resolver;
        _logger = logger;
        _notifier = notifier;
    }

    public string Id => TaskId;
    public string DisplayName => "Upload";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Primary input is bag.payload_bytes — the canonical "what to upload". For the URL
        // shortener category specifically (category="url") we also accept bag.text as a fallback
        // so a workflow can chain plain text-producing upstream steps (Convert color → Shorten,
        // ConvertColor for a URL-encoded code → Shorten, etc.) without going through a payload
        // intermediary. Image / file / video uploaders still require bytes — they have no
        // sensible interpretation of "text" as a payload.
        var category = (string?)config?["category"];
        byte[] bytes;
        if (context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) && rawBytes is byte[] direct && direct.Length > 0)
        {
            bytes = direct;
        }
        else if (string.Equals(category, "url", StringComparison.OrdinalIgnoreCase)
                 && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) && rawText is string text && !string.IsNullOrEmpty(text))
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(text);
        }
        else
        {
            _logger.LogWarning("UploadTask: bag key '{Key}' missing or not byte[]; skipping", PipelineBagKeys.PayloadBytes);
            return;
        }

        var ext = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string e ? e : "bin";
        var uploaders = await ResolveUploadersAsync(config, ext, cancellationToken).ConfigureAwait(false);
        if (uploaders.Count == 0)
        {
            _logger.LogWarning("UploadTask: no uploader available (none configured/enabled for this step).");
            return;
        }

        var contentType = ContentTypeFor(ext);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"arestoys-{stamp}.{ext.TrimStart('.')}";

        var urls = new List<string>(uploaders.Count);
        string? firstId = null;
        foreach (var uploader in uploaders)
        {
            var result = await uploader.UploadAsync(new UploadRequest(bytes, fileName, contentType), cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogError("UploadTask: '{Id}' failed: {Error}", uploader.Id, result.ErrorMessage);
                continue;
            }
            urls.Add(result.Url!);
            firstId ??= uploader.Id;
            _logger.LogInformation("UploadTask: '{Id}' uploaded {Bytes} bytes → {Url}", uploader.Id, bytes.Length, result.Url);
        }

        if (urls.Count == 0) return;

        // bag.text is the single source of truth for the URL produced by an upload — both
        // OpenUrl, UpdateItemUrl, QrCode and the ToastBuilder read it from here. The legacy
        // upload_url / upload_urls keys were removed in 0.1.17 (only ever held a duplicate of
        // bag.text and the multi-URL slot had zero consumers since the multi-upload feature was
        // collapsed in 0.1.16). bag.uploader_id stays as the "did an upload step actually run"
        // sentinel that UpdateItemUrl gates on, and as the per-item attribution downstream.
        context.Bag[PipelineBagKeys.UploaderId] = firstId!;
        context.Bag[PipelineBagKeys.Text] = urls[0];

        if ((bool?)config?["showNotification"] == true && _notifier is not null)
        {
            _notifier.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
    }

    private async Task<IReadOnlyList<IUploader>> ResolveUploadersAsync(JsonNode? config, string fileExtension, CancellationToken cancellationToken)
    {
        // 1. Explicit uploader id (new primary path — set by the consolidated "Upload to cloud
        //    service" / "Shorten URL" workflow descriptors which expose a dropdown of ids).
        var uploaderId = (string?)config?["uploader"];
        if (!string.IsNullOrEmpty(uploaderId))
        {
            var uploader = await _resolver.ResolveAsync(uploaderId, cancellationToken).ConfigureAwait(false);
            return uploader is null ? [] : [uploader];
        }

        // 2. Legacy category mode (kept for back-compat with user workflows that still carry the
        //    pre-refactor "category" config). Runs every uploader in the user's per-category list.
        var category = (string?)config?["category"];
        if (!string.IsNullOrEmpty(category) && TryParseCategory(category, out var caps))
        {
            return await _resolver.ResolveCategoryAsync(caps, cancellationToken).ConfigureAwait(false);
        }

        // 3. Auto-detect: derive the category from the payload's file extension and pick the
        //    first uploader the user has selected for that category. Empty string in config
        //    means "let the pipeline figure out the right destination" — what the new
        //    consolidated descriptor's default is.
        var detected = DetectCategory(fileExtension);
        if (detected != UploaderCapabilities.None)
        {
            var list = await _resolver.ResolveCategoryAsync(detected, cancellationToken).ConfigureAwait(false);
            return list.Count > 0 ? new[] { list[0] } : (IReadOnlyList<IUploader>)[];
        }
        return [];
    }

    private static UploaderCapabilities DetectCategory(string ext) => ext.ToLowerInvariant() switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff" => UploaderCapabilities.Image,
        "mp4" or "mov" or "webm" or "mkv" or "avi" or "wmv" or "m4v" => UploaderCapabilities.Video,
        "txt" or "md" or "log" or "csv" or "json" or "xml" or "html" or "htm" or "yaml" or "yml" => UploaderCapabilities.Text,
        _ => UploaderCapabilities.File,
    };

    private static bool TryParseCategory(string raw, out UploaderCapabilities category)
    {
        switch (raw.ToLowerInvariant())
        {
            case "image": category = UploaderCapabilities.Image; return true;
            case "file":  category = UploaderCapabilities.File;  return true;
            case "text":  category = UploaderCapabilities.Text;  return true;
            case "video": category = UploaderCapabilities.Video; return true;
            case "url":   category = UploaderCapabilities.Url;   return true;
            default:      category = UploaderCapabilities.None;  return false;
        }
    }

    private static string ContentTypeFor(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "mp4" => "video/mp4",
        "txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
