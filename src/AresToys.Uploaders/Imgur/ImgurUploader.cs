using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Imgur;

/// <summary>Anonymous Imgur upload — uses the bundled <see cref="Secrets.ImgurClientId"/>
/// (set in the maintainer's <c>Secrets.Local.cs</c>) and POSTs the file with an
/// <c>Authorization: Client-ID …</c> header. No user setup, no OAuth. For account-bound uploads
/// (image lands in user's library, album support) use <see cref="ImgurUserUploader"/> instead.</summary>
public sealed class ImgurUploader : IUploader
{
    private const string EndpointUrl = "https://api.imgur.com/3/upload";

    private readonly HttpClient _http;
    private readonly ILogger<ImgurUploader> _logger;

    public ImgurUploader(HttpClient http, ILogger<ImgurUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<ImgurUploader>.Instance;
    }

    public string Id => "imgur";
    public string DisplayName => "Imgur (anonymous)";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Image;

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(Secrets.ImgurClientId))
            return UploadResult.Failure("Imgur isn't configured in this build of AresToys. The maintainer must ship a Secrets.Local.cs with ImgurClientId.");

        using var form = new MultipartFormDataContent
        {
            { UploaderHttp.BuildFileContent(request.Bytes, request.ContentType), "image", request.FileName },
        };
        // Mirror the imgur.com web-client request shape exactly: client_id in the query string
        // (not the Authorization header), Origin set to https://imgur.com, Accept-Language hinted.
        // Imgur's WAF flags requests that look "off" compared to the web client, so the closer we
        // stay to the reference shape, the less we get false-positive bot-rejected.
        var url = $"{EndpointUrl}?client_id={Uri.EscapeDataString(Secrets.ImgurClientId)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        httpRequest.Headers.TryAddWithoutValidation("Origin", "https://imgur.com");
        httpRequest.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Imgur HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(ExtractError(body) ?? $"HTTP {(int)response.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("link", out var link)
                && link.ValueKind == JsonValueKind.String)
            {
                // webm uploads come back with a trailing dot — same quirk ShareX trims.
                return UploadResult.Success(link.GetString()!.TrimEnd('.'));
            }
            return UploadResult.Failure(ExtractError(body) ?? "Imgur response missing data.link");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Imgur network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Imgur JSON parse error");
            return UploadResult.Failure($"Invalid response: {ex.Message}");
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("error", out var err))
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
