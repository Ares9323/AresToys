using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Pastebin;

/// <summary>Pastebin text upload via the public API. User supplies an <c>api_dev_key</c> (free
/// at pastebin.com/doc_api). Privacy and expiration are exposed as dropdowns; everything else
/// (account login, syntax highlighting, raw URL toggle) is intentionally minimal — that surface
/// can grow when the host UI needs it.</summary>
public sealed class PastebinUploader : IUploader, IConfigurableUploader
{
    private const string EndpointUrl = "https://pastebin.com/api/api_post.php";
    private const string ApiKeyKey = "api_dev_key";
    private const string PrivacyKey = "privacy";
    private const string ExpirationKey = "expiration";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly ILogger<PastebinUploader> _logger;

    public PastebinUploader(HttpClient http, IPluginConfigStore config, ILogger<PastebinUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? NullLogger<PastebinUploader>.Instance;
    }

    public string Id => "pastebin";
    public string DisplayName => "Pastebin";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Text;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(ApiKeyKey, "API Developer Key",
            Description: "Get your unique key at https://pastebin.com/doc_api (requires a free account).",
            Sensitive: true),
        new DropdownSetting(PrivacyKey, "Privacy",
        [
            new DropdownOption("1", "Unlisted"),
            new DropdownOption("0", "Public"),
            new DropdownOption("2", "Private (logged-in only)"),
        ]),
        new DropdownSetting(ExpirationKey, "Expiration",
        [
            new DropdownOption("N", "Never"),
            new DropdownOption("10M", "10 minutes"),
            new DropdownOption("1H", "1 hour"),
            new DropdownOption("1D", "1 day"),
            new DropdownOption("1W", "1 week"),
            new DropdownOption("2W", "2 weeks"),
            new DropdownOption("1M", "1 month"),
        ]),
    ];

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = await _config.GetAsync(ApiKeyKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return UploadResult.Failure("Pastebin API key is not configured.");

        var privacy = await _config.GetAsync(PrivacyKey, cancellationToken).ConfigureAwait(false) ?? "1";
        var expiration = await _config.GetAsync(ExpirationKey, cancellationToken).ConfigureAwait(false) ?? "N";
        var text = Encoding.UTF8.GetString(request.Bytes);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["api_dev_key"] = apiKey,
            ["api_option"] = "paste",
            ["api_paste_code"] = text,
            ["api_paste_name"] = Path.GetFileNameWithoutExtension(request.FileName ?? string.Empty),
            ["api_paste_private"] = privacy,
            ["api_paste_expire_date"] = expiration,
        });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl) { Content = form };
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pastebin HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body);
            }
            // Pastebin returns either a URL on success or "Bad API request, ..." on logical failure
            // — both with HTTP 200. Treat anything that doesn't parse as a URL as an error.
            return Uri.TryCreate(body, UriKind.Absolute, out _)
                ? UploadResult.Success(body)
                : UploadResult.Failure(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Pastebin network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
    }
}
