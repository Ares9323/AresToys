using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.PluginContracts;

namespace AresToys.CustomUploaders;

/// <summary>An <see cref="IUploader"/> implementation backed by a <see cref="CustomUploaderConfig"/>.
/// Translates the declarative <c>.sxcu</c> shape into an <see cref="HttpRequestMessage"/>, sends
/// it via the injected <see cref="HttpClient"/>, then resolves the URL template against the
/// response body. One instance per .sxcu file; <see cref="Id"/> is derived from
/// <see cref="CustomUploaderConfig.Name"/> + a stable hash of the file path so duplicates of the
/// same name from different folders don't collide.</summary>
public sealed class CustomUploader : IUploader
{
    private readonly CustomUploaderConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<CustomUploader> _logger;

    public CustomUploader(CustomUploaderConfig config, string id, HttpClient http, ILogger<CustomUploader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.RequestURL))
            throw new ArgumentException("CustomUploaderConfig.RequestURL is required.", nameof(config));

        _config = config;
        _http = http;
        _logger = logger ?? NullLogger<CustomUploader>.Instance;
        Id = id;
        DisplayName = string.IsNullOrWhiteSpace(config.Name) ? id : config.Name!;
        Capabilities = MapDestinationType(config.DestinationType);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public UploaderCapabilities Capabilities { get; }

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Pre-request templating: file name + payload-as-text are available before we have a
        // response. Headers / parameters / arguments / data all get pre-templated in one pass;
        // post-templating happens once on the response URL. {input} is populated when the
        // uploader is text- or URL-capable — both consume textual payloads (text content for
        // Text, a URL string for Url). For binary uploaders we pass null so the token renders
        // as empty rather than producing mojibake.
        var inputAsText = (Capabilities & (UploaderCapabilities.Text | UploaderCapabilities.Url)) != 0
            ? Encoding.UTF8.GetString(request.Bytes)
            : null;
        var pre = new CustomUploaderTemplate(request.FileName, inputAsText);
        var url = AppendQueryString(pre.Apply(_config.RequestURL), pre.ApplyAll(_config.Parameters));
        var method = ParseMethod(_config.RequestMethod);

        using var httpRequest = new HttpRequestMessage(method, url)
        {
            // Force HTTP/1.1. Several .sxcu-friendly endpoints (Catbox, Litterbox, plain shares
            // behind Cloudflare) reset the TCP connection mid-upload when HttpClient negotiates
            // HTTP/2 — this is a known interop issue with how some reverse proxies handle large
            // multipart bodies over h2. Pinning the request to 1.1 matches what ShareX itself does.
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
        };
        // Disable Expect: 100-continue. Default behaviour is to send the headers, wait for a
        // "100 Continue" interim response, then send the body. Some hosts (and Cloudflare in
        // particular) just don't reply, the client times out, and the body never goes out.
        // Sending the body straight away costs nothing extra and avoids that whole class of
        // mysterious upload failures.
        httpRequest.Headers.ExpectContinue = false;
        // Default User-Agent: literal "ShareX". Two reasons:
        //  1. WAFs in front of common .sxcu services (BunkerWeb on litterbox.catbox.moe,
        //     Cloudflare on a bunch of others) recognise this string and pass the request —
        //     they're tuned around the volume of legitimate ShareX users uploading every day.
        //     A custom UA with parentheses / URLs / unusual tokens often trips bot rules and
        //     gets a 400 / 403 before the request reaches the application.
        //  2. .sxcu is ShareX's format. We're a faithful client of it, so identifying as
        //     ShareX is the most accurate description of the request shape (multipart layout,
        //     header set, body conventions all mirror ShareX's HttpClient defaults).
        // Caller-supplied User-Agent in the .sxcu Headers wins — see ApplyHeaders.
        if (httpRequest.Headers.UserAgent.Count == 0)
        {
            httpRequest.Headers.UserAgent.ParseAdd("ShareX");
        }
        // Browser-like Accept so WAFs that key on "request looks like an API client"
        // don't downgrade us into the bot bucket. */* is the broadest possible accept and
        // matches what curl / wget / ShareX / browsers all send by default.
        if (httpRequest.Headers.Accept.Count == 0)
        {
            httpRequest.Headers.Accept.ParseAdd("*/*");
        }

        ApplyHeaders(httpRequest, pre.ApplyAll(_config.Headers));

        // Body content depends on the body kind. The file itself is added only by the
        // multipart / binary branches; JSON / FormUrlEncoded / None never include the raw bytes
        // (those bodies are the "metadata-only" variants used by services that take the file via
        // a separate base64 field or a pre-signed URL flow).
        httpRequest.Content = BuildContent(request, pre);

        try
        {
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var post = new CustomUploaderTemplate(request.FileName, inputAsText, body);

            if (!response.IsSuccessStatusCode)
            {
                var errorTpl = string.IsNullOrWhiteSpace(_config.ErrorMessage) ? body : _config.ErrorMessage!;
                var rendered = post.Apply(errorTpl);
                var message = string.IsNullOrWhiteSpace(rendered) ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" : rendered;
                _logger.LogWarning("Custom uploader {Id} HTTP {Status}: {Message}", Id, (int)response.StatusCode, message);
                return UploadResult.Failure(message);
            }

            // URL field is optional in .sxcu; ShareX falls back to the raw body when missing,
            // which covers plain-text uploaders like 0x0.st. Same convention here.
            var urlTemplate = string.IsNullOrWhiteSpace(_config.URL) ? "{response}" : _config.URL!;
            var resolvedUrl = post.Apply(urlTemplate).Trim();
            if (string.IsNullOrEmpty(resolvedUrl))
            {
                var errMsg = string.IsNullOrWhiteSpace(_config.ErrorMessage)
                    ? "Upload succeeded but the URL template produced an empty result."
                    : post.Apply(_config.ErrorMessage!);
                return UploadResult.Failure(errMsg);
            }
            return UploadResult.Success(resolvedUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Custom uploader {Id} HTTP request failed", Id);
            return UploadResult.Failure($"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Failure("Upload cancelled.");
        }
    }

    private HttpContent? BuildContent(UploadRequest request, CustomUploaderTemplate template)
    {
        var bodyKind = (_config.Body ?? "MultipartFormData").Trim();
        switch (bodyKind.ToLowerInvariant())
        {
            case "multipartformdata":
            case "multipart":
                return BuildMultipartContent(request, template);
            case "formurlencoded":
            case "form":
                return BuildFormUrlEncoded(template);
            case "json":
            {
                var data = template.Apply(_config.Data);
                var content = new StringContent(data, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return content;
            }
            case "xml":
            {
                var data = template.Apply(_config.Data);
                var content = new StringContent(data, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
                return content;
            }
            case "binary":
            {
                var content = new ByteArrayContent(request.Bytes);
                if (!string.IsNullOrWhiteSpace(request.ContentType)
                    && MediaTypeHeaderValue.TryParse(request.ContentType, out var mt))
                {
                    content.Headers.ContentType = mt;
                }
                return content;
            }
            case "none":
                return null;
            default:
                _logger.LogWarning("Custom uploader {Id}: unknown body kind '{Kind}', falling back to multipart", Id, bodyKind);
                return BuildMultipartContent(request, template);
        }
    }

    private MultipartFormDataContent BuildMultipartContent(UploadRequest request, CustomUploaderTemplate template)
    {
        var form = new MultipartFormDataContent();
        // Templated text parts first (auth tokens, captions, etc.) — order doesn't matter HTTP-wise
        // but keeps payloads predictable when something logs them.
        foreach (var (key, value) in template.ApplyAll(_config.Arguments))
        {
            form.Add(new StringContent(value, Encoding.UTF8), key);
        }
        var fileFormName = string.IsNullOrWhiteSpace(_config.FileFormName) ? "file" : _config.FileFormName!;
        var fileContent = new ByteArrayContent(request.Bytes);
        if (!string.IsNullOrWhiteSpace(request.ContentType)
            && MediaTypeHeaderValue.TryParse(request.ContentType, out var mt))
        {
            fileContent.Headers.ContentType = mt;
        }
        form.Add(fileContent, fileFormName, request.FileName);
        return form;
    }

    private FormUrlEncodedContent BuildFormUrlEncoded(CustomUploaderTemplate template)
    {
        var pairs = template.ApplyAll(_config.Arguments);
        return new FormUrlEncodedContent(pairs);
    }

    /// <summary>Append <paramref name="parameters"/> to <paramref name="url"/> as a query string.
    /// Existing query-string portion is preserved.</summary>
    private static string AppendQueryString(string url, IDictionary<string, string> parameters)
    {
        if (parameters.Count == 0) return url;
        var separator = url.Contains('?') ? '&' : '?';
        var sb = new StringBuilder(url);
        var first = true;
        foreach (var (k, v) in parameters)
        {
            sb.Append(first ? separator : '&');
            sb.Append(WebUtility.UrlEncode(k));
            sb.Append('=');
            sb.Append(WebUtility.UrlEncode(v));
            first = false;
        }
        return sb.ToString();
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            // Some headers (Content-Type) belong on the content, not the request — TryAddWithoutValidation
            // returns false for those and we silently skip; the body branches set Content-Type on
            // their own content already.
            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                // No-op — content headers are owned by the body builders.
            }
        }
    }

    private static HttpMethod ParseMethod(string? method) => (method?.Trim().ToUpperInvariant()) switch
    {
        "GET"    => HttpMethod.Get,
        "PUT"    => HttpMethod.Put,
        "PATCH"  => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        _        => HttpMethod.Post,
    };

    private static UploaderCapabilities MapDestinationType(string? destinationType) =>
        (destinationType?.Trim().ToLowerInvariant()) switch
        {
            "imageuploader"      => UploaderCapabilities.Image,
            "textuploader"       => UploaderCapabilities.Text,
            "fileuploader"       => UploaderCapabilities.File,
            // URL shorteners and URL-sharing services both consume a URL and produce a URL —
            // the dedicated Url category routes them via the "Shorten clipboard URL" workflow
            // instead of dumping the URL as a text file via the Text category.
            "urlshortener"       => UploaderCapabilities.Url,
            "urlsharingservice"  => UploaderCapabilities.Url,
            _                    => UploaderCapabilities.AnyFile,
        };
}
