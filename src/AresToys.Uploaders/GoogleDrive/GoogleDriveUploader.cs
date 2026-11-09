using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.Uploaders.GoogleDrive;

/// <summary>Google Drive uploader using the v3 files API. The Google upload protocol takes
/// a <c>multipart/related</c> body (RFC 2387) with two parts: a JSON metadata blob (file name,
/// parent folder) followed by the raw binary. After upload we optionally POST a permission
/// (<c>role=reader, type=anyone</c>) so the returned <c>webViewLink</c> works without sign-in.
///
/// Setup: register an OAuth client at console.cloud.google.com → APIs &amp; Services →
/// Credentials → "Desktop app". Add <c>http://localhost</c> as a redirect URI. Enable the
/// Google Drive API for the project. Paste Client ID + Client Secret into the dialog. Scope is
/// <c>drive.file</c> (per-file access — the app only sees what it creates) plus
/// <c>userinfo.email</c> for the signed-in label.</summary>
public sealed class GoogleDriveUploader : IUploader, IConfigurableUploader, IOAuthUploader
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,webViewLink,webContentLink";
    private const string DriveFilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string Scope = "https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.email";

    private const string FolderNameKey = "folder_name";
    private const string AutoShareKey = "auto_share_link";
    private const string DirectLinkKey = "direct_link";
    /// <summary>Single source of truth for the default folder name — pre-fills the Configure
    /// dialog and acts as runtime fallback when the stored value is empty. Drive resolves the
    /// name to an ID via find-or-create at upload time (Drive APIs need IDs, not names).</summary>
    private const string DefaultFolder = "AresToys";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly OAuthFlowService _oauth;
    private readonly ILogger<GoogleDriveUploader> _logger;

    public GoogleDriveUploader(HttpClient http, IPluginConfigStore config, OAuthFlowService oauth, ILogger<GoogleDriveUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _oauth = oauth;
        _logger = logger ?? NullLogger<GoogleDriveUploader>.Instance;
    }

    public string Id => "googledrive";
    public string DisplayName => "Google Drive";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(FolderNameKey, "Upload folder",
            Description: "Folder name in your Drive root. Auto-created on first upload if missing. Empty = upload to root.",
            Default: DefaultFolder),
        new BoolSetting(AutoShareKey, "Create shareable link",
            Description: "When on, returns a URL anyone can open without signing in. When off, returns the private webViewLink that only works while you're logged into your Google account.",
            Default: true),
        new BoolSetting(DirectLinkKey, "Use direct link",
            Description: "Returns the file's webContentLink (direct download / hot-link friendly) instead of the Drive preview page with header / download button.",
            Default: false),
    ];

    public OAuthAuthorizeRequest BuildOAuthRequest()
    {
        EnsureBundled();
        return new OAuthAuthorizeRequest
        {
            AuthorizationEndpoint = AuthorizationEndpoint,
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.GoogleDriveClientId,
            ClientSecret = Secrets.GoogleDriveClientSecret,
            Scope = Scope,
            UsePkce = true,
            // access_type=offline is the only way Google ever returns a refresh_token. prompt=consent
            // forces the consent screen even on re-auth so the user sees current scopes and a fresh
            // refresh_token gets issued (otherwise Google omits it on subsequent sign-ins).
            ExtraAuthorizeParams = new Dictionary<string, string>
            {
                ["access_type"] = "offline",
                ["prompt"] = "consent",
            },
        };
    }

    public OAuthRefreshRequest BuildRefreshRequest(string refreshToken)
    {
        EnsureBundled();
        return new OAuthRefreshRequest
        {
            TokenEndpoint = TokenEndpoint,
            ClientId = Secrets.GoogleDriveClientId,
            ClientSecret = Secrets.GoogleDriveClientSecret,
            RefreshToken = refreshToken,
        };
    }

    public async Task<string?> GetSignedInDisplayNameAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
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

        var storedFolder = (await _config.GetAsync(FolderNameKey, cancellationToken).ConfigureAwait(false))?.Trim();
        var folderName = string.IsNullOrEmpty(storedFolder) ? DefaultFolder : storedFolder;
        var autoShare = !bool.TryParse(await _config.GetAsync(AutoShareKey, cancellationToken).ConfigureAwait(false), out var b) || b;
        var directLink = bool.TryParse(await _config.GetAsync(DirectLinkKey, cancellationToken).ConfigureAwait(false), out var d) && d;

        try
        {
            // Drive APIs need parent IDs, not names — resolve the configured folder name to an
            // ID via find-or-create. Skipping this step (folderName empty) uploads to My Drive
            // root which is also fine.
            string? folderId = null;
            if (!string.IsNullOrEmpty(folderName))
                folderId = await ResolveOrCreateFolderAsync(accessToken, folderName, cancellationToken).ConfigureAwait(false);

            var file = await UploadFileAsync(accessToken, request, folderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Empty response from Google Drive upload.");

            if (autoShare)
            {
                // Best-effort permissions update — if it fails the file is still uploaded, just
                // private. Don't kill the whole upload over it.
                try { await SetPublicAsync(accessToken, file.Id, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "GoogleDrive: failed to set public permission, link will require sign-in"); }
            }

            // Direct link → webContentLink ("https://drive.google.com/uc?id=…&export=download" — direct
            // file content, hot-linkable). Otherwise webViewLink (the Drive preview page).
            var url = directLink ? file.WebContentLink : file.WebViewLink;
            return string.IsNullOrEmpty(url)
                ? UploadResult.Failure("Google Drive returned no link of the requested type.")
                : UploadResult.Success(url!);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GoogleDrive network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return UploadResult.Failure(ex.Message);
        }
    }

    private async Task<GoogleDriveFile?> UploadFileAsync(string accessToken, UploadRequest request, string? folderId, CancellationToken cancellationToken)
    {
        var metadataObj = string.IsNullOrEmpty(folderId)
            ? (object)new { name = request.FileName }
            : new { name = request.FileName, parents = new[] { folderId } };
        var metadataJson = JsonSerializer.Serialize(metadataObj);

        // multipart/related with two parts — JSON metadata then the file bytes — per Google's
        // simple multipart upload spec. Note: NOT multipart/form-data, the parts have no field
        // names, just sequential Content-Type headers.
        using var multi = new MultipartContent("related");
        var metadataPart = new StringContent(metadataJson, Encoding.UTF8, "application/json");
        multi.Add(metadataPart);
        var filePart = UploaderHttp.BuildFileContent(request.Bytes, request.ContentType);
        multi.Add(filePart);

        using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = multi };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("GoogleDrive upload HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException(ExtractGoogleError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        return new GoogleDriveFile(
            Id: doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            WebViewLink: doc.RootElement.TryGetProperty("webViewLink", out var wv) ? wv.GetString() : null,
            WebContentLink: doc.RootElement.TryGetProperty("webContentLink", out var wc) ? wc.GetString() : null);
    }

    /// <summary>Look up a folder by name in My Drive root and return its ID, creating it if it
    /// doesn't exist. Drive's only "name → ID" mapping is via list-with-query, so we POST a
    /// search and POST a create if the search comes back empty. Two requests in the worst case,
    /// one in the typical case (folder already exists). Folder lookup is scoped to root via
    /// <c>'root' in parents</c> so two same-named folders nested elsewhere don't confuse us.</summary>
    private async Task<string?> ResolveOrCreateFolderAsync(string accessToken, string folderName, CancellationToken cancellationToken)
    {
        // Single-quotes inside the folder name need escaping per Drive's query syntax — backslash
        // before the quote. Drive's parser is lenient otherwise.
        var safeName = folderName.Replace("'", "\\'");
        var query = $"mimeType='application/vnd.google-apps.folder' and name='{safeName}' and trashed=false and 'root' in parents";
        var listUrl = $"{DriveFilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=files(id,name)&pageSize=1";

        using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(listReq);

        using var listResp = await _http.SendAsync(listReq, cancellationToken).ConfigureAwait(false);
        var listBody = await listResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!listResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("GoogleDrive folder lookup HTTP {Status}: {Body}", (int)listResp.StatusCode, listBody);
            // Soft fail: upload to root instead of bombing out — the user gets the file uploaded
            // even if we couldn't resolve their preferred folder.
            return null;
        }
        using (var doc = JsonDocument.Parse(listBody))
        {
            if (doc.RootElement.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0
                && files[0].TryGetProperty("id", out var existingId)
                && existingId.ValueKind == JsonValueKind.String)
            {
                return existingId.GetString();
            }
        }

        // Folder missing — create it under My Drive root with the canonical folder mimeType.
        var createPayload = JsonSerializer.Serialize(new
        {
            name = folderName,
            mimeType = "application/vnd.google-apps.folder",
        });
        using var createContent = new StringContent(createPayload, Encoding.UTF8, "application/json");
        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{DriveFilesEndpoint}?fields=id") { Content = createContent };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(createReq);

        using var createResp = await _http.SendAsync(createReq, cancellationToken).ConfigureAwait(false);
        var createBody = await createResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!createResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("GoogleDrive folder create HTTP {Status}: {Body}", (int)createResp.StatusCode, createBody);
            return null;
        }
        using var createDoc = JsonDocument.Parse(createBody);
        return createDoc.RootElement.TryGetProperty("id", out var newId) && newId.ValueKind == JsonValueKind.String
            ? newId.GetString()
            : null;
    }

    private async Task SetPublicAsync(string accessToken, string fileId, CancellationToken cancellationToken)
    {
        var url = $"{DriveFilesEndpoint}/{Uri.EscapeDataString(fileId)}/permissions";
        var json = JsonSerializer.Serialize(new { role = "reader", type = "anyone" });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        UploaderHttp.ApplyDefaults(req);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(ExtractGoogleError(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
    }

    private static void EnsureBundled()
    {
        if (string.IsNullOrWhiteSpace(Secrets.GoogleDriveClientId) || string.IsNullOrWhiteSpace(Secrets.GoogleDriveClientSecret))
            throw new InvalidOperationException("Google Drive isn't configured in this build of AresToys. The maintainer must ship a Secrets.Local.cs with GoogleDriveClientId + GoogleDriveClientSecret.");
    }

    private static string? ExtractGoogleError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private sealed record GoogleDriveFile(string Id, string? WebViewLink, string? WebContentLink);
}
