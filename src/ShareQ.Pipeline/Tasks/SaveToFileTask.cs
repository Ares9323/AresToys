using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ShareQ.Core.Pipeline;
using ShareQ.Storage.Settings;

namespace ShareQ.Pipeline.Tasks;

public sealed class SaveToFileTask : IPipelineTask
{
    public const string TaskId = "shareq.save-to-file";
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\ShareQ";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";

    private readonly ISettingsStore _settings;
    private readonly ILogger<SaveToFileTask> _logger;

    public SaveToFileTask(ISettingsStore settings, ILogger<SaveToFileTask> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Save to file";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var rawBytes) || rawBytes is not byte[] bytes)
        {
            _logger.LogWarning("SaveToFileTask: bag key '{Key}' missing or not byte[]; skipping", PipelineBagKeys.PayloadBytes);
            return;
        }

        var extension = context.Bag.TryGetValue(PipelineBagKeys.FileExtension, out var rawExt) && rawExt is string ext
            ? ext
            : "bin";

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
            var sub = ExpandPatternTokens(Environment.ExpandEnvironmentVariables(subPatternRaw), DateTime.Now);
            folder = Path.Combine(folder, sub);
        }
        Directory.CreateDirectory(folder);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var titleSlug = context.Bag.TryGetValue(PipelineBagKeys.WindowTitle, out var rawTitle) && rawTitle is string title
            ? SanitizeForFilename(title)
            : string.Empty;
        var baseName = string.IsNullOrEmpty(titleSlug)
            ? $"shareq-{stamp}"
            : $"shareq-{titleSlug}-{stamp}";
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
        _logger.LogDebug("SaveToFileTask: wrote {Bytes} bytes to {Path}", bytes.Length, fullPath);
    }

    /// <summary>ShareX-style date / metadata tokens for the sub-folder pattern. Tokens use the
    /// same prefix style as ShareX (<c>%y</c>, <c>%mo</c>, <c>%d</c>, <c>%h</c>, <c>%mi</c>,
    /// <c>%s</c>, <c>%yy</c>, <c>%pm</c>) so users migrating from ShareX recognise them.</summary>
    private static string ExpandPatternTokens(string pattern, DateTime now) => pattern
        .Replace("%yyyy", now.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%yy",   now.ToString("yy",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%y",    now.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%mo",   now.ToString("MM",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%mon",  now.ToString("MMMM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%d",    now.ToString("dd",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%h",    now.ToString("HH",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%mi",   now.ToString("mm",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%s",    now.ToString("ss",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%pm",   now.ToString("tt",   CultureInfo.InvariantCulture), StringComparison.Ordinal);

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
