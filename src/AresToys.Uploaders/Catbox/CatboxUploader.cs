using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Catbox;

/// <summary>Anonymous file upload to <c>catbox.moe</c> — POST multipart, response is the URL as
/// plain text. No auth, no rate limit metadata. Identical request shape to the ShareX bundled
/// Catbox uploader, so the two are interchangeable on the wire.</summary>
public sealed class CatboxUploader : IUploader
{
    private const string EndpointUrl = "https://catbox.moe/user/api.php";

    private readonly HttpClient _http;
    private readonly ILogger<CatboxUploader> _logger;

    public CatboxUploader(HttpClient http, ILogger<CatboxUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<CatboxUploader>.Instance;
    }

    public string Id => "catbox";
    public string DisplayName => "Catbox";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("fileupload"), "reqtype" },
            { UploaderHttp.BuildFileContent(request.Bytes, request.ContentType), "fileToUpload", request.FileName },
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = form };
        // Same WAF (BunkerWeb) as Litterbox — same Origin/Referer/Sec-Fetch-* trick to look
        // like a legit browser submitting the catbox.moe upload form.
        httpRequest.Headers.TryAddWithoutValidation("Origin", "https://catbox.moe");
        httpRequest.Headers.TryAddWithoutValidation("Referer", "https://catbox.moe/");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Catbox HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? UploadResult.Success(body)
                : UploadResult.Failure(string.IsNullOrEmpty(body) ? "Empty response" : body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Catbox network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
