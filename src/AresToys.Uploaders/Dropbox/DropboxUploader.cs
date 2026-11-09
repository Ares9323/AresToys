using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.Uploaders.Dropbox;

/// <summary>Dropbox uploader against API v2. Upload protocol is the slightly unusual "RPC over
/// header": the file bytes go in the body as <c>application/octet-stream</c> while the per-call
/// JSON metadata (path, conflict mode, mute notifications) travels in a <c>Dropbox-API-Arg</c>
/// header. After upload, an optional <c>sharing/create_shared_link_with_settings</c> POST gets a
/// public URL; toggling "direct link" rewrites the share URL to the dl.dropboxusercontent.com
/// mirror so it serves the file inline instead of the share-page chrome.
///
/// Setup: register an app at <c>dropbox.com/developers/apps</c>, choose "Scoped access", "Full
/// Dropbox" (or "App folder" if you prefer), enable <c>files.content.write</c> + <c>sharing.write</c>
/// (and <c>account_info.read</c> for the signed-in label). Add <c>http://localhost</c> as a
/// redirect URI. Paste Client ID + (optional) Client Secret into the dialog.</summary>
public sealed class DropboxUploader : IUploader, IConfigurableUploader, IOAuthUploader
{
    private const string AuthorizationEndpoint = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";
    private const string UploadEndpoint = "https://content.dropboxapi.com/2/files/upload";
    private const string ShareEndpoint = "https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings";
    private const string AccountEndpoint = "https://api.dropboxapi.com/2/users/get_current_account";
    // Empty scope falls back to the app-level scopes selected in the developer console.
    private const string Scope = "files.content.write sharing.write account_info.read";

    private const string FolderKey = "folder";
    private const string AutoShareKey = "auto_share_link";
    private const string DirectLinkKey = "direct_link";
    /// <summary>Default subfolder name. <see cref="BuildPath"/> trims leading/trailing slashes
    /// so "AresToys" and "/AresToys" behave identically — we go with the no-slash form because
    /// that's what Dropbox shows in its UI when the folder is created. App-folder-scoped apps
    /// will see this nested as <c>/Apps/&lt;AppName&gt;/AresToys/</c> (a redundancy if the app
    /// is also named AresToys); user can wipe the field to upload directly into the app root.</summary>
    private const string DefaultFolder = "AresToys";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly OAuthFlowService _oauth;
    private readonly ILogger<DropboxUploader> _logger;

    public DropboxUploader(HttpClient http, IPluginConfigStore config, OAuthFlowService oauth, ILogger<DropboxUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _oauth = oauth;
        _logger = logger ?? NullLogger<DropboxUploader>.Instance;
    }

    public string Id => "dropbox";
    public string DisplayName => "Dropbox";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(FolderKey, "Upload folder",
            Description: "Subfolder under your Dropbox root (or under your App folder for App-folder-scoped apps). Created automatically on first upload. Empty = root.",
            Default: DefaultFolder),
        new BoolSetting(AutoShareKey, "Create shareable link",
            Description: "When on, returns a URL anyone can open without signing in. When off, returns a private dropbox.com URL that only works while you're logged into your Dropbox account.",
            Default: true),
        new BoolSetting(DirectLinkKey, "Use direct link",
            Description: "Rewrites the share URL to dl.dropboxusercontent.com so it serves the file content directly (good for hot-linking) instead of the Dropbox share page with header / download button.",
            Default: true),
    ];

    // Dropbox demands byte-exact redirect_uri match (no loopback "any port" rule like Azure/Google).
    // Pinned to 53682 — same well-known port rclone uses, so users with existing firewall rules
    // for that tool already cover AresToys too. The user has to register http://localhost:53682/
    // (with the trailing slash) in the app's OAuth 2 redirect URIs.
    private const int DropboxFixedPort = 53682;

    public OAuthAuthorizeRequest BuildOAuthRequest()
    {
        EnsureBundled();
        return new OAuthAuthorizeRequest
        {
            AuthorizationEndpoint = AuthorizationEndpoint,
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.DropboxClientId,
            ClientSecret = string.IsNullOrEmpty(Secrets.DropboxClientSecret) ? null : Secrets.DropboxClientSecret,
            Scope = Scope,
            UsePkce = true,
            LoopbackPort = DropboxFixedPort,
            // token_access_type=offline is the new (post-2021) Dropbox flag that makes the auth
            // server issue a refresh_token. Without it tokens expire after 4 hours and there's
            // no way to get a new one without re-prompting the user.
            ExtraAuthorizeParams = new Dictionary<string, string> { ["token_access_type"] = "offline" },
        };
    }

    public OAuthRefreshRequest BuildRefreshRequest(string refreshToken)
    {
        EnsureBundled();
        return new OAuthRefreshRequest
        {
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.DropboxClientId,
            ClientSecret = string.IsNullOrEmpty(Secrets.DropboxClientSecret) ? null : Secrets.DropboxClientSecret,
            RefreshToken = refreshToken,
        };
    }

    public async Task<string?> GetSignedInDisplayNameAsync(string accessToken, CancellationToken cancellationToken)
    {
        // get_current_account is a POST with body literally "null" (per Dropbox docs for no-arg
        // RPC calls). The endpoint also accepts an empty body — we send "null" because that's
        // what their docs show in every example.
        using var content = new StringContent("null", Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, AccountEndpoint) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
            return e.GetString();
        return null;
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Bytes.LongLength > 150_000_000)
            return UploadResult.Failure("Dropbox simple upload caps at 150 MB. Larger files would need the chunked /upload_session API.");

        string accessToken;
        try
        {
            accessToken = await OAuthTokenStore.GetValidAccessTokenAsync(_config, _oauth,
                refreshToken => BuildRefreshRequest(refreshToken), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return UploadResult.Failure(ex.Message); }

        var storedFolder = (await _config.GetAsync(FolderKey, cancellationToken).ConfigureAwait(false))?.Trim();
        var folder = string.IsNullOrEmpty(storedFolder) ? DefaultFolder : storedFolder;
        var autoShare = !bool.TryParse(await _config.GetAsync(AutoShareKey, cancellationToken).ConfigureAwait(false), out var b) || b;
        var directLink = !bool.TryParse(await _config.GetAsync(DirectLinkKey, cancellationToken).ConfigureAwait(false), out var d) || d;
        var path = BuildPath(folder, request.FileName);

        try
        {
            var uploadedPath = await UploadFileAsync(accessToken, path, request, cancellationToken).ConfigureAwait(false);
            if (!autoShare)
            {
                // No public share requested — link to the parent folder so the user lands in
                // their Dropbox UI showing the file. /home/<path> works for FOLDERS but not
                // FILES, hence we strip the filename. NOTE: only works when the Dropbox app is
                // registered as "Full Dropbox" type — App folder apps return paths relative to
                // /Apps/<AppName>/ and our /home/<path> won't match the real Dropbox root.
                // For App folder users this URL still resolves to the bare /home (Dropbox quietly
                // drops the unknown path), which is at least openable.
                var lastSlash = uploadedPath.LastIndexOf('/');
                var parentPath = lastSlash <= 0 ? string.Empty : uploadedPath[..lastSlash];
                var safeParent = string.Join('/', parentPath.Split('/').Select(Uri.EscapeDataString));
                return UploadResult.Success($"https://www.dropbox.com/home{safeParent}");
            }

            var shareUrl = await CreateShareableLinkAsync(accessToken, uploadedPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(shareUrl))
                return UploadResult.Failure("Dropbox upload succeeded but share-link creation returned no URL.");
            return UploadResult.Success(directLink ? ToDirectLink(shareUrl) : shareUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Dropbox network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return UploadResult.Failure(ex.Message);
        }
    }

    private async Task<string> UploadFileAsync(string accessToken, string path, UploadRequest request, CancellationToken cancellationToken)
    {
        // Dropbox's "RPC-over-header" pattern: file body is octet-stream, the per-call args go
        // in Dropbox-API-Arg as JSON. The header value MUST be ASCII; STJ's default escaping
        // emits \uXXXX for any non-ASCII character which keeps the header valid even when the
        // file name has accents or emoji.
        var arg = JsonSerializer.Serialize(new
        {
            path,
            mode = "overwrite",
            autorename = false,
            mute = true,
        });
        using var content = new ByteArrayContent(request.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Dropbox upload HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException(ExtractDropboxError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        // path_display preserves original capitalization; path_lower is folded — share endpoint
        // accepts either, we use path_display to keep the URL readable.
        return doc.RootElement.TryGetProperty("path_display", out var pd) ? pd.GetString() ?? path : path;
    }

    private async Task<string?> CreateShareableLinkAsync(string accessToken, string path, CancellationToken cancellationToken)
    {
        // Default audience for newly-issued Dropbox shared links is "team_only" (Business
        // accounts) or unset = "no_one" on stricter setups. Both surface as "It seems you don't
        // belong here! You should probably sign in" 403s when opened from a browser without
        // matching credentials. We force audience=public so anyone with the URL can view —
        // matches the "auto-share = on" intent. requested_visibility=public is the legacy
        // synonym; sending both keeps us compatible with older API behaviour.
        var json = JsonSerializer.Serialize(new
        {
            path,
            settings = new
            {
                audience = "public",
                access = "viewer",
                requested_visibility = "public",
            },
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, ShareEndpoint) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // shared_link_already_exists is recoverable: re-call list_shared_links to fetch the
            // existing one. For simplicity we surface the error and let the user re-upload —
            // AresToys's mode="overwrite" means this only happens on identical re-uploads anyway.
            _logger.LogWarning("Dropbox share HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException(ExtractDropboxError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    private static string ToDirectLink(string shareUrl)
    {
        // Standard Dropbox share URL is https://www.dropbox.com/scl/fi/<id>/<name>?rlkey=...&st=...&dl=0
        // The query string is NOT decoration — rlkey + st are the share's auth tokens. Without
        // them the file returns 403 ("you don't belong here, sign in") regardless of audience
        // settings. Earlier versions stripped the query and broke every direct-link upload.
        // The host swap to dl.dropboxusercontent.com forces inline content delivery (no Dropbox
        // chrome) without needing ?dl=1, which can otherwise trigger Save-As dialogs.
        return shareUrl.Replace("https://www.dropbox.com/", "https://dl.dropboxusercontent.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPath(string folder, string fileName)
    {
        // Dropbox paths are /-prefixed, /-separated, no escaping required — the JSON encoder
        // handles UTF-8. Normalise: strip leading/trailing slashes from the folder, then join.
        var name = fileName.Replace('\\', '/');
        if (string.IsNullOrEmpty(folder)) return "/" + name;
        var f = folder.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(f) ? "/" + name : "/" + f + "/" + name;
    }

    private static void EnsureBundled()
    {
        if (string.IsNullOrWhiteSpace(Secrets.DropboxClientId))
            throw new InvalidOperationException("Dropbox isn't configured in this build of AresToys. The maintainer must ship a Secrets.Local.cs with DropboxClientId.");
    }

    private static string? ExtractDropboxError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Dropbox wraps errors as { "error_summary": "...", "error": { ".tag": "..." } }
            if (doc.RootElement.TryGetProperty("error_summary", out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();
            if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
