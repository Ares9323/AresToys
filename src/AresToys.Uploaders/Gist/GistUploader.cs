using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Gist;

/// <summary>GitHub Gist text upload using a Personal Access Token. We require a PAT instead of
/// OAuth because OAuth needs an embedded client secret + a callback listener; a PAT is one field
/// in Settings and matches how every other GitHub-touching CLI tool authenticates. Token needs
/// the <c>gist</c> scope (classic) or <c>Gists: read &amp; write</c> (fine-grained).</summary>
public sealed class GistUploader : IUploader, IConfigurableUploader
{
    private const string EndpointUrl = "https://api.github.com/gists";
    private const string TokenKey = "personal_access_token";
    private const string PublicKey = "public";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly ILogger<GistUploader> _logger;

    public GistUploader(HttpClient http, IPluginConfigStore config, ILogger<GistUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? NullLogger<GistUploader>.Instance;
    }

    public string Id => "gist";
    public string DisplayName => "GitHub Gist";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Text;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(TokenKey, "Personal Access Token",
            Description: "Create a token at https://github.com/settings/tokens with the 'gist' scope.",
            Placeholder: "ghp_...",
            Sensitive: true),
        new BoolSetting(PublicKey, "Public gist",
            Description: "Off = secret gist (still URL-accessible, just unlisted).",
            Default: false),
    ];

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await _config.GetAsync(TokenKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return UploadResult.Failure("GitHub Personal Access Token is not configured.");

        var publicRaw = await _config.GetAsync(PublicKey, cancellationToken).ConfigureAwait(false);
        var isPublic = bool.TryParse(publicRaw, out var b) && b;
        var text = Encoding.UTF8.GetString(request.Bytes);
        var fileName = string.IsNullOrEmpty(request.FileName) ? "snippet.txt" : Path.GetFileName(request.FileName);

        var payload = JsonSerializer.Serialize(new
        {
            description = string.Empty,
            @public = isPublic,
            files = new Dictionary<string, object>
            {
                [fileName] = new { content = text },
            },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = content };
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        httpRequest.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gist HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(ExtractError(body) ?? $"HTTP {(int)response.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("html_url", out var url)
                && url.ValueKind == JsonValueKind.String)
            {
                return UploadResult.Success(url.GetString()!);
            }
            return UploadResult.Failure("Gist response missing html_url");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Gist network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Gist JSON parse error");
            return UploadResult.Failure($"Invalid response: {ex.Message}");
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
