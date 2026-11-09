using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.SharedFolder;

/// <summary>"Upload" by writing the file to a local or UNC path. Useful for self-hosted /
/// NAS workflows: drop screenshots into a folder served by Caddy/nginx/IIS and the URL
/// returned is the public address of the freshly-written file. With no <see cref="UrlPrefixKey"/>
/// configured we fall back to a <c>file://</c> URI — works on the local machine but not for
/// sharing with anyone else.
///
/// Simplified vs ShareX's <c>SharedFolderUploader</c>: that one includes HTTP-serving config
/// (port, subfolder pattern, HTTP home path, no-extension toggle, browser protocol picker)
/// that bloated the surface for a marginal benefit. We collapse to two settings — folder and
/// URL prefix — and let the user write a richer URL prefix if they want path templating
/// (e.g. <c>https://my.nas/screenshots</c>).</summary>
public sealed class SharedFolderUploader : IUploader, IConfigurableUploader
{
    private const string TargetFolderKey = "target_folder";
    private const string UrlPrefixKey = "url_prefix";

    private readonly IPluginConfigStore _config;
    private readonly ILogger<SharedFolderUploader> _logger;

    public SharedFolderUploader(IPluginConfigStore config, ILogger<SharedFolderUploader>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<SharedFolderUploader>.Instance;
    }

    public string Id => "shared-folder";
    public string DisplayName => "Shared folder (local / UNC)";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(TargetFolderKey, "Target folder",
            Description: "Where to write the file. Local path (e.g. D:\\Public\\Shares) or UNC (e.g. \\\\nas\\screenshots). Created automatically if it doesn't exist.",
            Placeholder: @"\\nas\screenshots"),
        new StringSetting(UrlPrefixKey, "Public URL prefix (optional)",
            Description: "Prepended to the filename to build the returned URL — set this when the target folder is served by a web server (e.g. https://my.nas/screenshots). Leave empty to return a file:// URI usable only on this machine.",
            Placeholder: "https://my.nas/screenshots"),
    ];

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetFolder = (await _config.GetAsync(TargetFolderKey, cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(targetFolder))
            return UploadResult.Failure("Shared folder isn't configured. Set the target folder in the Configure dialog.");

        var urlPrefix = (await _config.GetAsync(UrlPrefixKey, cancellationToken).ConfigureAwait(false))?.Trim();
        var safeFileName = SanitizeFileName(request.FileName);

        try
        {
            // Expand ~ / %USERPROFILE% / %APPDATA% style folder variables before writing — same
            // user expectation as Save dialogs everywhere on Windows.
            var expandedFolder = Environment.ExpandEnvironmentVariables(targetFolder);
            Directory.CreateDirectory(expandedFolder);
            var destPath = Path.Combine(expandedFolder, safeFileName);

            // Overwrite quietly: workflow normally produces unique timestamped names anyway,
            // and a freshly-pasted same-named file is more likely the user retrying than data
            // they want to preserve.
            await File.WriteAllBytesAsync(destPath, request.Bytes, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SharedFolder: wrote {Bytes} bytes to {Path}", request.Bytes.Length, destPath);

            return UploadResult.Success(BuildPublicUrl(urlPrefix, destPath, safeFileName));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "SharedFolder: write denied");
            return UploadResult.Failure($"Write denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "SharedFolder: I/O error");
            return UploadResult.Failure($"I/O error: {ex.Message}");
        }
    }

    /// <summary>Two URL shapes: explicit web prefix (the share-via-NAS case) vs file:// fallback
    /// (mostly useful for local-machine shortcuts and email-the-path workflows). The web prefix
    /// path uses URL-encoded filename so spaces / accents survive the click.</summary>
    private static string BuildPublicUrl(string? urlPrefix, string destPath, string fileName)
    {
        if (!string.IsNullOrEmpty(urlPrefix))
        {
            var prefix = urlPrefix.TrimEnd('/');
            return $"{prefix}/{Uri.EscapeDataString(fileName)}";
        }
        // file:// URI: convert backslashes to forward slashes and ensure the leading triple-slash
        // for absolute Windows paths. Uri's own constructor handles drive letters + UNC correctly.
        return new Uri(destPath).AbsoluteUri;
    }

    /// <summary>Strip path separators / NUL / ASCII control chars from the candidate filename so
    /// a malicious bag value can't escape the target folder. Path.Combine alone doesn't protect
    /// against ".." segments — we rely on SanitizeFileName + the fact that pipeline-supplied
    /// names are timestamp-based, never user-typed at the upload step.</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "arestoys-upload.bin";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return sb.ToString();
    }

}
