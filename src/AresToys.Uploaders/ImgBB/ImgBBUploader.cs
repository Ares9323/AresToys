using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.ImgBB;

/// <summary>ImgBB anonymous image host. User supplies an API key (free at
/// <c>api.imgbb.com</c>). Multipart POST with the file under <c>image</c>; the API key goes in
/// the query string per the documented protocol.</summary>
public sealed class ImgBBUploader : IUploader, IConfigurableUploader
{
    private const string EndpointUrl = "https://api.imgbb.com/1/upload";
    private const string ApiKeyKey = "api_key";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly ILogger<ImgBBUploader> _logger;

    public ImgBBUploader(HttpClient http, IPluginConfigStore config, ILogger<ImgBBUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? NullLogger<ImgBBUploader>.Instance;
    }

    public string Id => "imgbb";
    public string DisplayName => "ImgBB";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Image;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(ApiKeyKey, "API Key",
            Description: "Get a free API key at https://api.imgbb.com.",
            Placeholder: "32-character key",
            Sensitive: true),
    ];

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = await _config.GetAsync(ApiKeyKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return UploadResult.Failure("ImgBB API key is not configured.");

        var url = $"{EndpointUrl}?key={Uri.EscapeDataString(apiKey)}";
        using var form = new MultipartFormDataContent
        {
            { UploaderHttp.BuildFileContent(request.Bytes, request.ContentType), "image", request.FileName },
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ImgBB HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(ExtractError(body) ?? $"HTTP {(int)response.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("url", out var direct)
                && direct.ValueKind == JsonValueKind.String)
            {
                return UploadResult.Success(direct.GetString()!);
            }
            return UploadResult.Failure(ExtractError(body) ?? "ImgBB response missing data.url");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "ImgBB network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ImgBB JSON parse error");
            return UploadResult.Failure($"Invalid response: {ex.Message}");
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                return err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : err.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
