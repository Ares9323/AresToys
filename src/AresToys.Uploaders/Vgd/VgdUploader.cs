using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Vgd;

/// <summary>v.gd anonymous URL shortener — sister service of is.gd by the same operator. Same
/// API shape (GET <c>create.php?format=simple&amp;url=…</c>, plain-text response), different
/// host. Useful as a fallback when is.gd's rate limit kicks in or for users who prefer a
/// shorter prefix domain.</summary>
public sealed class VgdUploader : IUploader
{
    private const string EndpointBase = "https://v.gd/create.php?format=simple&url=";

    private readonly HttpClient _http;
    private readonly ILogger<VgdUploader> _logger;

    public VgdUploader(HttpClient http, ILogger<VgdUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<VgdUploader>.Instance;
    }

    public string Id => "v.gd";
    public string DisplayName => "v.gd";
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
                _logger.LogWarning("v.gd HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? UploadResult.Success(body)
                : UploadResult.Failure(string.IsNullOrEmpty(body) ? "Empty response" : body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "v.gd network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
