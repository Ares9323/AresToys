using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Imaging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Settings;

namespace AresToys.Pipeline.Tasks;

public sealed class SaveToFileTask : IPipelineTask
{
    public const string TaskId = "arestoys.save-to-file";
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";
    private const string JpegQualitySettingKey = "capture.jpeg_quality";

    private readonly ISettingsStore _settings;
    private readonly IImageEncoder? _encoder;
    private readonly AresToys.Core.Pipeline.IPipelineNotifier? _notifier;
    private readonly ILogger<SaveToFileTask> _logger;

    public SaveToFileTask(ISettingsStore settings, ILogger<SaveToFileTask> logger, IImageEncoder? encoder = null, AresToys.Core.Pipeline.IPipelineNotifier? notifier = null)
    {
        _settings = settings;
        _encoder = encoder;
        _logger = logger;
        _notifier = notifier;
    }

    public string Id => TaskId;
    public string DisplayName => "Save as Image file";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Opt-in "only run if an editor / generator actually modified the bytes". Lets the user
        // chain two SaveToFile steps around an OpenEditorBeforeUpload to get a before/after pair
        // without producing a duplicate file when nothing was edited. Reads the
        // bag.payload_modified flag set by OpenEditorBeforeUpload on a successful save.
        var skipIfNotModified = (bool?)config?["skipIfNotModified"] ?? false;
        if (skipIfNotModified && !context.Bag.ContainsKey(PipelineBagKeys.PayloadModified))
        {
            _logger.LogDebug("SaveToFileTask: skipIfNotModified=true and bag.payload_modified is absent — skipping.");
            return;
        }

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) || rawBytes is not byte[] bytes)
        {
            _logger.LogWarning("SaveToFileTask: bag key '{Key}' missing or not byte[]; skipping", PipelineBagKeys.PayloadBytes);
            return;
        }

        var extension = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string ext
            ? ext
            : "bin";

        // Optional per-step format override. When set, the task re-encodes the bag's image
        // bytes into the requested format before writing — handy for workflows that need a
        // specific output regardless of the global capture preference (e.g. a JPEG-only Imgur
        // workflow). Null / unrecognised → keep the bag bytes verbatim. Re-encode only fires
        // when (a) the encoder service is available, (b) the override differs from the bag's
        // current extension. The PayloadBytes in the bag is mutated so downstream steps
        // (CopyImageToClipboard, AddToHistory) see the same transformed bytes.
        var formatOverrideRaw = (string?)config?["format"];
        var formatOverride = ImageFormatExtensions.TryParse(formatOverrideRaw);
        if (formatOverride is not null && _encoder is not null)
        {
            var targetExt = formatOverride.Value.ToExtension();
            if (!string.Equals(targetExt, extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var rawQuality = await _settings.GetAsync(JpegQualitySettingKey, cancellationToken).ConfigureAwait(false);
                    var quality = int.TryParse(rawQuality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)
                        ? Math.Clamp(q, 1, 100) : 90;
                    var encoded = _encoder.Encode(bytes, formatOverride.Value, quality);
                    bytes = encoded;
                    extension = targetExt;
                    context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
                    context.Bag[PipelineBagKeys.FileExtension] = extension;
                    _logger.LogDebug("SaveToFileTask: re-encoded {OldExt} → {NewExt} ({NewKb} KB)",
                        rawExt, extension, bytes.Length / 1024);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SaveToFileTask: format override to {Format} failed — writing original bytes",
                        formatOverride);
                }
            }
        }

        // Order of precedence: explicit step config → user setting (capture.folder) → default.
        var folderTemplate = (string?)config?["folder"]
            ?? await _settings.GetAsync(FolderSettingKey, cancellationToken).ConfigureAwait(false)
            ?? DefaultFolder;
        var folder = Environment.ExpandEnvironmentVariables(folderTemplate);

        // Optional sub-folder pattern (ShareX-style tokens). Applied as a relative path appended
        // to the base folder. Empty / missing pattern = no sub-folder. The pattern goes through
        // the same env-var expansion so things like "%USERPROFILE%\extra\%y" still work, then the
        // ShareX tokens are substituted.
        var subPatternRaw = (string?)config?["subfolder_pattern"]
            ?? await _settings.GetAsync(SubFolderPatternSettingKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subPatternRaw))
        {
            var sub = DatePatternExpander.Expand(Environment.ExpandEnvironmentVariables(subPatternRaw), DateTime.Now);
            folder = Path.Combine(folder, sub);
        }
        Directory.CreateDirectory(folder);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var titleSlug = context.Bag.TryGetValue(PipelineBagKeys.WindowTitle, out var rawTitle) && rawTitle is string title
            ? SanitizeForFilename(title)
            : string.Empty;
        var baseName = string.IsNullOrEmpty(titleSlug)
            ? $"arestoys-{stamp}"
            : $"arestoys-{titleSlug}-{stamp}";
        var bareExt = extension.TrimStart('.');
        var fullPath = Path.Combine(folder, $"{baseName}.{bareExt}");

        // Collision guard: when a workflow runs Save-to-file twice with Apply-effects in
        // between (one save for the original, one for the modified), both writes can land
        // in the same millisecond on fast hardware → the second clobbers the first. Append
        // -1, -2, … until we find a free slot. Cheap (just a stat call per attempt) and
        // bounded — file systems realistically never need more than a few iterations.
        if (File.Exists(fullPath))
        {
            for (var n = 1; n < 1000; n++)
            {
                var candidate = Path.Combine(folder, $"{baseName}-{n}.{bareExt}");
                if (!File.Exists(candidate))
                {
                    fullPath = candidate;
                    break;
                }
            }
        }

        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);

        context.Bag[PipelineBagKeys.LocalPath] = fullPath;
        context.Bag[PipelineBagKeys.Text] = fullPath;
        _logger.LogDebug("SaveToFileTask: wrote {Bytes} bytes to {Path}", bytes.Length, fullPath);

        if ((bool?)config?["showNotification"] == true && _notifier is not null)
        {
            // When the user enabled "Only save if image was edited", the toast appears AFTER an
            // upstream Open-editor step has already closed — surfacing "Open in editor" would just
            // re-open the same image the user just finished editing. Suppress it so the toast keeps
            // only the useful Copy path / Show in folder actions.
            _notifier.ShowFromBag(
                context,
                (string?)config?["notificationTitle"],
                suppressEditorButton: skipIfNotModified);
        }
    }

    /// <summary>ShareX-style date / metadata tokens for the sub-folder pattern. Tokens use the
    /// same prefix style as ShareX (<c>%y</c>, <c>%mo</c>, <c>%d</c>, <c>%h</c>, <c>%mi</c>,
    /// <c>%s</c>, <c>%yy</c>, <c>%pm</c>) so users migrating from ShareX recognise them.</summary>
    private static string SanitizeForFilename(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '-' || c == ' ') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim('_');
        return s.Length > 40 ? s[..40] : s;
    }
}
