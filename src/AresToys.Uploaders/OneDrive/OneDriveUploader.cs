using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.Uploaders.OneDrive;

/// <summary>OneDrive file uploader against Microsoft Graph v1.0. The user signs in with their
/// Microsoft account through <see cref="OAuthFlowService"/> (loopback redirect), then every
/// upload PUTs the bytes to <c>/me/drive/root:/{folder}/{name}:/content</c>; if "auto-create
/// shareable link" is enabled, a follow-up POST to <c>/createLink</c> gets a public anonymous
/// view URL. PKCE on, client secret optional (Azure "public client" apps work without one).
///
/// Setup: the user has to register their own application in Azure Portal → App registrations,
/// add <c>http://localhost</c> as a "Mobile and desktop applications" redirect URI, and
/// (under API permissions) grant <c>Files.ReadWrite</c> + <c>offline_access</c>. The app's
/// Client ID goes into the settings dialog. We don't ship a built-in Client ID because (a) it'd
/// need a tenant we manage, (b) a single shared one would hit Azure rate limits across all users.</summary>
public sealed class OneDriveUploader : IUploader, IConfigurableUploader, IOAuthUploader
{
    private const string AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";
    // OAuth scope: offline_access => refresh_token; Files.ReadWrite => upload + create-link.
    private const string Scope = "offline_access Files.ReadWrite";

    private const string FolderKey = "folder";
    private const string AutoShareKey = "auto_share_link";
    private const string DirectLinkKey = "direct_link";
    /// <summary>Single source of truth for the default folder — used both as initial UI value
    /// and as runtime fallback so the user gets the same behavior whether or not they ever
    /// opened the Configure dialog.</summary>
    private const string DefaultFolder = "AresToys";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly OAuthFlowService _oauth;
    private readonly ILogger<OneDriveUploader> _logger;

    public OneDriveUploader(HttpClient http, IPluginConfigStore config, OAuthFlowService oauth, ILogger<OneDriveUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _oauth = oauth;
        _logger = logger ?? NullLogger<OneDriveUploader>.Instance;
    }

    public string Id => "onedrive";
    public string DisplayName => "OneDrive";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(FolderKey, "Upload folder",
            Description: "Path under your OneDrive root. Created automatically on first upload.",
            Default: DefaultFolder),
        new BoolSetting(AutoShareKey, "Create shareable link",
            Description: "When on, returns a URL anyone can open without signing in. When off, returns the private webUrl that only works while you're logged into your OneDrive account.",
            Default: true),
        new BoolSetting(DirectLinkKey, "Use direct link",
            Description: "Returns an embeddable URL that serves the file content (good for <img src=\"…\"> hot-linking) instead of the OneDrive preview page with header / download button.",
            Default: false),
    ];

    // --- IOAuthUploader ---

    public OAuthAuthorizeRequest BuildOAuthRequest()
    {
        EnsureBundled();
        return new OAuthAuthorizeRequest
        {
            AuthorizationEndpoint = AuthorizationEndpoint,
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.OneDriveClientId,
            ClientSecret = string.IsNullOrEmpty(Secrets.OneDriveClientSecret) ? null : Secrets.OneDriveClientSecret,
            Scope = Scope,
            UsePkce = true,
        };
    }

    public OAuthRefreshRequest BuildRefreshRequest(string refreshToken)
    {
        EnsureBundled();
        return new OAuthRefreshRequest
        {
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.OneDriveClientId,
            ClientSecret = string.IsNullOrEmpty(Secrets.OneDriveClientSecret) ? null : Secrets.OneDriveClientSecret,
            RefreshToken = refreshToken,
        };
    }

    public async Task<string?> GetSignedInDisplayNameAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Graph /me returns the signed-in user's profile. UPN is the most useful identifier for
        // a "signed in as ..." label; fall back to displayName.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphRoot}/me?$select=displayName,userPrincipalName");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("userPrincipalName", out var upn) && upn.ValueKind == JsonValueKind.String)
            return upn.GetString();
        if (doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
            return dn.GetString();
        return null;
    }

    // --- Upload ---

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string accessToken;
        try
        {
            accessToken = await OAuthTokenStore.GetValidAccessTokenAsync(_config, _oauth,
                refreshToken => BuildRefreshRequest(refreshToken), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return UploadResult.Failure(ex.Message);
        }

        var storedFolder = (await _config.GetAsync(FolderKey, cancellationToken).ConfigureAwait(false))?.Trim();
        var folder = string.IsNullOrEmpty(storedFolder) ? DefaultFolder : storedFolder;
        var autoShare = !bool.TryParse(await _config.GetAsync(AutoShareKey, cancellationToken).ConfigureAwait(false), out var b) || b;
        var directLink = bool.TryParse(await _config.GetAsync(DirectLinkKey, cancellationToken).ConfigureAwait(false), out var d) && d;
        var uploadPath = BuildUploadPath(folder, request.FileName);

        try
        {
            var uploadInfo = await UploadFileAsync(accessToken, uploadPath, request, cancellationToken).ConfigureAwait(false);
            if (uploadInfo is null)
                return UploadResult.Failure("OneDrive upload returned an empty response.");

            string url;
            if (autoShare)
            {
                // type=embed when DirectLink is on → returns the URL of an iframe-embeddable
                // resource that serves the file content directly. type=view (default) returns
                // the OneDrive preview page that needs a click-through to actually see the file.
                var shared = await CreateShareableLinkAsync(accessToken, uploadInfo.Id, directLink ? "embed" : "view", cancellationToken).ConfigureAwait(false);
                url = shared ?? uploadInfo.WebUrl ?? string.Empty;
            }
            else
            {
                url = uploadInfo.WebUrl ?? string.Empty;
            }
            return string.IsNullOrEmpty(url)
                ? UploadResult.Failure("OneDrive upload succeeded but no URL was returned.")
                : UploadResult.Success(url);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OneDrive network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return UploadResult.Failure(ex.Message);
        }
    }

    private async Task<OneDriveItemInfo?> UploadFileAsync(string accessToken, string uploadPath, UploadRequest request, CancellationToken cancellationToken)
    {
        // Direct PUT works up to ~250 MiB per Graph docs; for screenshots / typical share-files
        // that's plenty. Larger files would need createUploadSession + chunked PUTs — punted to
        // a future task once someone actually hits the limit.
        var url = $"{GraphRoot}/me/drive/root:/{uploadPath}:/content";
        using var content = UploaderHttp.BuildFileContent(request.Bytes, request.ContentType);
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OneDrive upload HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException(ExtractGraphError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new OneDriveItemInfo(
            Id: root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            WebUrl: root.TryGetProperty("webUrl", out var w) ? w.GetString() : null);
    }

    private async Task<string?> CreateShareableLinkAsync(string accessToken, string itemId, string linkType, CancellationToken cancellationToken)
    {
        var url = $"{GraphRoot}/me/drive/items/{Uri.EscapeDataString(itemId)}/createLink";
        var payload = JsonSerializer.Serialize(new { type = linkType, scope = "anonymous" });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OneDrive createLink HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            return null; // non-fatal — we fall back to webUrl in the caller
        }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("link", out var link)
            && link.TryGetProperty("webUrl", out var w)
            && w.ValueKind == JsonValueKind.String)
        {
            return w.GetString();
        }
        return null;
    }

    private static string BuildUploadPath(string folder, string fileName)
    {
        // The Graph "root:/{path}:/content" addressing wants forward slashes, no leading slash,
        // and each segment URL-encoded individually (otherwise '/' in the encoded form would be
        // re-encoded as %2F and the path would collapse into one segment).
        var safeName = Uri.EscapeDataString(fileName);
        if (string.IsNullOrEmpty(folder)) return safeName;
        var segments = folder.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            sb.Append(Uri.EscapeDataString(seg)).Append('/');
        }
        sb.Append(safeName);
        return sb.ToString();
    }

    private static void EnsureBundled()
    {
        // Bundled credentials live in Secrets.cs (placeholder, empty) which the .csproj swaps
        // for Secrets.Local.cs (real values, gitignored) when the maintainer ships a build.
        // No per-user override surface — fork + rebuild for that.
        if (string.IsNullOrWhiteSpace(Secrets.OneDriveClientId))
            throw new InvalidOperationException("OneDrive isn't configured in this build of AresToys. The maintainer must ship a Secrets.Local.cs with OneDriveClientId.");
    }

    private static string? ExtractGraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private sealed record OneDriveItemInfo(string Id, string? WebUrl);
}
