using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.PasteRs;

/// <summary>paste.rs — minimalist text paste host. POSTs the raw bytes (no multipart, no form),
/// returns the URL as plain text. Text-only by capability so the host's "Image" / "File"
/// destinations don't accidentally pick it.</summary>
public sealed class PasteRsUploader : IUploader
{
    private const string EndpointUrl = "https://paste.rs";

    private readonly HttpClient _http;
    private readonly ILogger<PasteRsUploader> _logger;

    public PasteRsUploader(HttpClient http, ILogger<PasteRsUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<PasteRsUploader>.Instance;
    }

    public string Id => "paste.rs";
    public string DisplayName => "paste.rs";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Text;

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var content = UploaderHttp.BuildFileContent(request.Bytes, request.ContentType);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = content };
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("paste.rs HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? UploadResult.Success(body)
                : UploadResult.Failure(string.IsNullOrEmpty(body) ? "Empty response" : body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "paste.rs network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
