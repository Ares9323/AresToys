using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.UguuSe;

/// <summary>Uguu.se temporary file host. Uploads via multipart with the file under <c>files[]</c>;
/// the <c>?output=text</c> query forces a plain-text URL response so we don't need to parse the
/// default JSON body. Files expire after ~3 hours.</summary>
public sealed class UguuSeUploader : IUploader
{
    private const string EndpointUrl = "https://uguu.se/upload?output=text";

    private readonly HttpClient _http;
    private readonly ILogger<UguuSeUploader> _logger;

    public UguuSeUploader(HttpClient http, ILogger<UguuSeUploader>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<UguuSeUploader>.Instance;
    }

    public string Id => "uguu.se";
    public string DisplayName => "Uguu.se";
    public UploaderCapabilities Capabilities => UploaderCapabilities.AnyFile;

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var form = new MultipartFormDataContent
        {
            { UploaderHttp.BuildFileContent(request.Bytes, request.ContentType), "files[]", request.FileName },
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = form };
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Uguu.se HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            return body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? UploadResult.Success(body)
                : UploadResult.Failure(string.IsNullOrEmpty(body) ? "Empty response" : body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Uguu.se network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
