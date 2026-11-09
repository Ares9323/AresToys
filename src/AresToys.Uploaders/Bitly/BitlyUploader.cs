using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.Bitly;

/// <summary>bit.ly URL shortener using a Generic Access Token (free at app.bitly.com → Settings
/// → API → Generic Access Token). PAT instead of full OAuth because OAuth needs an app
/// registration + client secret + callback handler — the PAT route is what every CLI/desktop
/// tool ends up at and bit.ly explicitly supports it.
///
/// Free tier: 100 short links / month per account; enough for casual use, not for bots.
/// Custom domain (Brand Short Domain, e.g. <c>your.brand.com</c>) supported via the optional
/// <see cref="DomainKey"/> setting — leave blank for the default <c>bit.ly</c> domain.</summary>
public sealed class BitlyUploader : IUploader, IConfigurableUploader
{
    private const string ShortenEndpoint = "https://api-ssl.bitly.com/v4/shorten";
    private const string TokenKey = "access_token";
    private const string DomainKey = "domain";
    private const string DefaultDomain = "bit.ly";

    private readonly HttpClient _http;
    private readonly IPluginConfigStore _config;
    private readonly ILogger<BitlyUploader> _logger;

    public BitlyUploader(HttpClient http, IPluginConfigStore config, ILogger<BitlyUploader>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? NullLogger<BitlyUploader>.Instance;
    }

    public string Id => "bitly";
    public string DisplayName => "bit.ly";
    public UploaderCapabilities Capabilities => UploaderCapabilities.Url;

    public IReadOnlyList<UploaderSetting> GetSettings() =>
    [
        new StringSetting(TokenKey, "Generic Access Token",
            Description: "Generate at https://app.bitly.com/settings/api/ — paste the token here. Free tier: 100 links/month.",
            Placeholder: "abcd1234…",
            Sensitive: true),
        new StringSetting(DomainKey, "Custom domain (optional)",
            Description: "Leave blank for the default bit.ly domain. Set to your Brand Short Domain (e.g. your.brand.com) if you have one configured on your bit.ly account.",
            Default: DefaultDomain),
    ];

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await _config.GetAsync(TokenKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return UploadResult.Failure("bit.ly Access Token is not configured.");

        var inputUrl = Encoding.UTF8.GetString(request.Bytes).Trim();
        if (string.IsNullOrEmpty(inputUrl))
            return UploadResult.Failure("No URL on the clipboard to shorten.");
        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out _))
            return UploadResult.Failure($"Not a valid absolute URL: {inputUrl}");

        var domain = (await _config.GetAsync(DomainKey, cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(domain)) domain = DefaultDomain;

        var payload = JsonSerializer.Serialize(new { long_url = inputUrl, domain });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ShortenEndpoint) { Content = content };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        UploaderHttp.ApplyDefaults(httpRequest);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("bit.ly HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return UploadResult.Failure(ExtractError(body) ?? $"HTTP {(int)response.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("link", out var link)
                && link.ValueKind == JsonValueKind.String)
            {
                return UploadResult.Success(link.GetString()!);
            }
            return UploadResult.Failure(ExtractError(body) ?? "bit.ly response missing 'link' field");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "bit.ly network error");
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "bit.ly JSON parse error");
            return UploadResult.Failure($"Invalid response: {ex.Message}");
        }
    }

    /// <summary>bit.ly errors are JSON: <c>{"message":"…","description":"…","resource":"…"}</c>.
    /// description is the more user-friendly field; fall back to message when it's missing.</summary>
    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
