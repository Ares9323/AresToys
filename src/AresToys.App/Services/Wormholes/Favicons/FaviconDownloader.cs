using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Real <see cref="IFaviconDownloader"/>. Source order (per design): try the site
/// directly first (privacy — only the destination host is contacted), and only fall back to a
/// third-party favicon service if the direct routes fail:
/// <list type="number">
///   <item><c>https://{host}/favicon.ico</c></item>
///   <item>fetch the page HTML and follow its <c>&lt;link rel="icon"&gt;</c></item>
///   <item><c>https://www.google.com/s2/favicons?domain={host}&amp;sz=64</c></item>
///   <item><c>https://icons.duckduckgo.com/ip3/{host}.ico</c></item>
/// </list>
/// Each candidate's bytes go through <see cref="IcoEncoder"/> so the result is always a valid
/// <c>.ico</c> (PNG payloads get wrapped). First success wins.</summary>
public sealed partial class FaviconDownloader : IFaviconDownloader
{
    // One shared client for the whole app: pools connections, and a per-request CTS gives us the
    // timeout without the socket-exhaustion of new-HttpClient-per-call. A browser-ish UA avoids
    // the odd 403 from servers that reject the default .NET agent.
    private static readonly HttpClient Http = CreateClient();

    private const int RequestTimeoutMs = 5000;
    private const int MaxBytes = 512 * 1024; // a favicon over 512 KB is almost certainly not one

    private readonly ILogger<FaviconDownloader> _logger;

    public FaviconDownloader(ILogger<FaviconDownloader> logger) => _logger = logger;

    public async Task<byte[]?> DownloadIcoAsync(string pageUrl, string host, CancellationToken ct)
    {
        // 1. Direct /favicon.ico
        var direct = await TryFetchIcoAsync($"https://{host}/favicon.ico", pageUrl, ct).ConfigureAwait(false);
        if (direct is not null) return direct;

        // 2. Parse the page's <link rel="icon"> and fetch whatever it points at.
        var fromHtml = await TryFromHtmlAsync(pageUrl, ct).ConfigureAwait(false);
        if (fromHtml is not null) return fromHtml;

        // 3. Google's favicon service (PNG, sized).
        var google = await TryFetchIcoAsync($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64", pageUrl, ct).ConfigureAwait(false);
        if (google is not null) return google;

        // 4. DuckDuckGo's favicon service (ICO).
        var ddg = await TryFetchIcoAsync($"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(host)}.ico", pageUrl, ct).ConfigureAwait(false);
        return ddg;
    }

    /// <summary>GET a candidate URL, cap the body, and convert to ICO. Returns null on any
    /// failure (non-success status, empty/oversize body, undecodable image).</summary>
    private async Task<byte[]?> TryFetchIcoAsync(string candidateUrl, string referer, CancellationToken ct)
    {
        var bytes = await TryGetBytesAsync(candidateUrl, referer, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        var ico = IcoEncoder.ToIco(bytes);
        if (ico is null)
            _logger.LogDebug("Favicon candidate {Url} returned undecodable image bytes", candidateUrl);
        return ico;
    }

    private async Task<byte[]?> TryFromHtmlAsync(string pageUrl, CancellationToken ct)
    {
        try
        {
            var html = await TryGetStringAsync(pageUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html)) return null;

            var href = ExtractIconHref(html);
            if (string.IsNullOrEmpty(href)) return null;

            if (!Uri.TryCreate(new Uri(pageUrl), href, out var iconUri)) return null;
            return await TryFetchIcoAsync(iconUri.ToString(), pageUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Favicon HTML probe failed for {Url}", pageUrl);
            return null;
        }
    }

    /// <summary>Find the href of the first <c>&lt;link&gt;</c> whose rel mentions "icon"
    /// (covers <c>icon</c>, <c>shortcut icon</c>, <c>apple-touch-icon</c>). Crude but adequate —
    /// a full HTML parser would be overkill for one attribute.</summary>
    internal static string? ExtractIconHref(string html)
    {
        foreach (Match m in LinkTagRegex().Matches(html))
        {
            var tag = m.Value;
            var rel = AttrRegex("rel").Match(tag);
            if (!rel.Success) continue;
            if (rel.Groups[1].Value.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var href = AttrRegex("href").Match(tag);
            if (href.Success && href.Groups[1].Value.Length > 0) return href.Groups[1].Value.Trim();
        }
        return null;
    }

    private static async Task<byte[]?> TryGetBytesAsync(string url, string referer, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);
            using var req = BuildRequest(url, referer);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
            return bytes;
        }
        catch
        {
            return null; // timeout, DNS, TLS, cancellation — all "no favicon from here"
        }
    }

    private static async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);
            using var req = BuildRequest(url, referer: null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
            // Only the <head> matters for the icon link; decode as UTF-8 (good enough to find the tag).
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage BuildRequest(string url, string? referer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        return req;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) AresToys/1.0");
        return client;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTagRegex();

    private static Regex AttrRegex(string name) =>
        new($@"{name}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
}
