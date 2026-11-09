using System.Net;
using System.Net.Http.Headers;

namespace AresToys.Uploaders;

/// <summary>Tunes an <see cref="HttpRequestMessage"/> with browser-like defaults (HTTP/1.1, no
/// Expect-100, Chrome/Windows User-Agent, Accept: */*, Accept-Language). Built-in uploaders go
/// through the same chrome so we present a consistent, "looks like a browser" signature on the
/// wire — modern WAFs (BunkerWeb, Cloudflare bot management, Akamai BMP) increasingly reject
/// requests that look programmatic, so we mimic Chrome to stay below the radar.
///
/// We tried a "ShareX" UA before — it worked for a while because BunkerWeb whitelists ShareX's
/// known patterns, then Litterbox's WAF tightened up and started flagging it too. Chrome UA is
/// the universally-accepted fallback every CLI/desktop tool ends up at.</summary>
internal static class UploaderHttp
{
    // Chrome on Windows 10/11. Bumped occasionally to track current-stable Chrome — outdated UAs
    // also get flagged by some WAFs.
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    /// <summary>Apply the standard headers + version policy to <paramref name="request"/> in
    /// place. Caller-supplied headers / UA are NOT overridden; this only fills in defaults
    /// for headers the request doesn't already have.</summary>
    public static void ApplyDefaults(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.ExpectContinue = false;

        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.ParseAdd(DefaultUserAgent);

        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.ParseAdd("*/*");

        if (request.Headers.AcceptLanguage.Count == 0)
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>Build a <see cref="ByteArrayContent"/> for a file payload, copying the request's
    /// content-type onto the part. Falls back to <c>application/octet-stream</c> when the
    /// caller didn't supply one (e.g. text uploads).</summary>
    public static ByteArrayContent BuildFileContent(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(contentType)
            && MediaTypeHeaderValue.TryParse(contentType, out var mt))
        {
            content.Headers.ContentType = mt;
        }
        else
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return content;
    }
}
