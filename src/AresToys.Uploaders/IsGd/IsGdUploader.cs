using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.IsGd;

/// <summary>is.gd anonymous URL shortener — GET request with the URL as a query param, response
/// is the shortened URL as plain text. No auth, modest rate limit (~one request per second per
/// IP, fine for interactive use). Sister service v.gd uses identical API on a different host;
/// see <see cref="VgdUploader"/>.</summary>
public sealed class IsGdUploader : IUploader
{
    private const string EndpointBase = "https://is.gd/create.php?format=simple&url=";

    private readonly HttpClient _http;
    private readonly ILogger<IsGdUploader> _logger;

    public IsGdUploader(HttpClient http, ILogger<IsGdUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<IsGdUploader>.Instance;
    }

    public string Id => "is.gd";
    public string DisplayName => "is.gd";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Url;

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputUrl = Encoding.UTF8.GetString(request.Bytes).Trim();
        if (string.IsNullOrEmpty(inputUrl))
            return UploadResult.Failure("No URL on the clipboard to shorten.");
        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out _))
            return UploadResult.Failure($"Not a valid absolute URL: {inputUrl}");

        var url = EndpointBase + Uri.EscapeDataString(inputUrl);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("is.gd HTTP {Status}: {Body}", (int)response.StatusCode, body);
                // is.gd returns descriptive text on 4xx (e.g. "Error: Please specify a URL to shorten."),
                // surface it as-is when the body is non-empty.
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            // Success format: plain text shortened URL on a single line. The "format=simple" query
            // param ensures we don't get the default HTML response.
            return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? UploadResult.Success(body)
                : UploadResult.Failure(string.IsNullOrEmpty(body) ? "Empty response" : body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "is.gd network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
