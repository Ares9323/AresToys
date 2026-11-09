using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AresToys.Uploaders.OAuth;

/// <summary>Generic OAuth2 authorization-code flow with optional PKCE. Same shape as ShareX's
/// <c>OAuthListener</c> + per-uploader <c>GetAuthorizationURL</c> / <c>GetAccessToken</c> pair,
/// rolled into one provider-agnostic service: caller hands in an <see cref="OAuthAuthorizeRequest"/>
/// and gets back an <see cref="OAuthToken"/> when the user finishes signing in. The actual token
/// storage / refresh-on-use cycle lives in <see cref="OAuthTokenStore"/>; this class is stateless.</summary>
public sealed class OAuthFlowService
{
    private readonly HttpClient _http;
    private readonly ILogger<OAuthFlowService> _logger;

    public OAuthFlowService(HttpClient http, ILogger<OAuthFlowService>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<OAuthFlowService>.Instance;
    }

    /// <summary>Run the full flow: pick a free loopback port, open the browser at the provider's
    /// authorize URL, wait for the callback hit, exchange the code for tokens. Cancellation lets
    /// the caller abort if the user closes the dialog mid-flow — the listener is disposed and the
    /// awaited task throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<OAuthToken> AuthorizeAsync(OAuthAuthorizeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Port: the uploader can pin a fixed one (Dropbox needs it — exact-match redirect_uri
        // policy) or leave it null and we grab a free random port (Azure, Google, Imgur all
        // accept any port via the OAuth loopback rule). Root path on purpose: providers register
        // http://localhost[:port] with NO path, then matching is exact path-wise. We use
        // 'localhost' (not 127.0.0.1) as the host because providers like Dropbox do NOT treat
        // them as equivalent — they want a byte-exact match — and 'localhost' is what every
        // dashboard auto-suggests. HttpListener accepts 'localhost' as a prefix without an
        // urlacl reservation (Windows treats it as loopback).
        var port = request.LoopbackPort ?? GetFreeLoopbackPort();
        var redirectUri = $"http://localhost:{port}/";
        var state = RandomBase64Url(32);
        var (verifier, challenge) = request.UsePkce ? GeneratePkcePair() : (null, null);

        var authorizeUrl = BuildAuthorizeUrl(request, redirectUri, state, challenge);
        _logger.LogInformation("OAuth: opening browser for {Endpoint}", request.AuthorizationEndpoint);

        // listener BEFORE opening the browser — otherwise a fast-loading callback could land before
        // we're ready to accept it.
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Couldn't bind loopback HTTP listener on port {port}. Run AresToys as the current user (not elevated) or check firewall rules.", ex);
        }

        OpenBrowser(authorizeUrl);

        // GetContextAsync isn't natively cancellable; wrap it so we can dispose the listener on
        // cancellation and bail out cleanly.
        HttpListenerContext context;
        var getContextTask = listener.GetContextAsync();
        using (cancellationToken.Register(() => { try { listener.Stop(); } catch { /* already disposed */ } }))
        {
            try { context = await getContextTask.ConfigureAwait(false); }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
        }

        var qs = context.Request.QueryString;
        var code = qs.Get("code");
        var receivedState = qs.Get("state");
        var error = qs.Get("error");
        var errorDescription = qs.Get("error_description");

        await WriteCallbackResponseAsync(context.Response,
            error is not null
                ? $"Authorization failed: {error}{(errorDescription is null ? "" : " — " + errorDescription)}"
                : receivedState != state
                    ? "State mismatch — possible CSRF, ignoring."
                    : "Authorization completed. You can close this tab and return to AresToys.").ConfigureAwait(false);

        if (error is not null)
            throw new InvalidOperationException($"OAuth provider returned error: {error}{(errorDescription is null ? "" : " — " + errorDescription)}");
        if (receivedState != state)
            throw new InvalidOperationException("OAuth state mismatch — request rejected.");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("OAuth callback did not include an authorization code.");

        return await ExchangeCodeForTokenAsync(request, redirectUri, code, verifier, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Use the long-lived <paramref name="request"/>.RefreshToken to get a fresh access
    /// token. Some providers (Google, Dropbox) don't rotate the refresh_token on every refresh;
    /// when the response omits one, the caller should keep the previous value rather than
    /// overwriting it with null. <see cref="OAuthTokenStore"/> handles that detail.</summary>
    public async Task<OAuthToken> RefreshAsync(OAuthRefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = request.RefreshToken,
            ["client_id"] = request.ClientId,
        };
        if (!string.IsNullOrEmpty(request.ClientSecret))
            form["client_secret"] = request.ClientSecret;

        return await PostTokenRequestAsync(request.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthToken> ExchangeCodeForTokenAsync(
        OAuthAuthorizeRequest request, string redirectUri, string code, string? verifier, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = request.ClientId,
        };
        if (!string.IsNullOrEmpty(request.ClientSecret))
            form["client_secret"] = request.ClientSecret;
        if (!string.IsNullOrEmpty(verifier))
            form["code_verifier"] = verifier;

        return await PostTokenRequestAsync(request.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthToken> PostTokenRequestAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        UploaderHttp.ApplyDefaults(httpRequest);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OAuth token endpoint HTTP {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Token endpoint returned HTTP {(int)response.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var token = OAuthToken.FromTokenResponse(doc.RootElement);
        if (string.IsNullOrEmpty(token.AccessToken))
            throw new InvalidOperationException($"Token endpoint response missing access_token: {body}");
        return token;
    }

    private static string BuildAuthorizeUrl(OAuthAuthorizeRequest request, string redirectUri, string state, string? challenge)
    {
        var qs = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = request.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = request.Scope,
            ["state"] = state,
        };
        if (!string.IsNullOrEmpty(challenge))
        {
            qs["code_challenge"] = challenge;
            qs["code_challenge_method"] = "S256";
        }
        if (request.ExtraAuthorizeParams is { Count: > 0 } extra)
        {
            foreach (var (k, v) in extra) qs[k] = v;
        }
        var sb = new StringBuilder(request.AuthorizationEndpoint);
        sb.Append(request.AuthorizationEndpoint.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var (k, v) in qs)
        {
            if (!first) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            first = false;
        }
        return sb.ToString();
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, string message)
    {
        // Plain HTML, no styling — the user only sees this for half a second before they close
        // the tab. Done before token exchange so the browser tab unblocks immediately.
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>AresToys</title></head><body style=\"font-family:system-ui;padding:2em\"><h2>AresToys</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.KeepAlive = false;
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            await response.OutputStream.FlushAsync().ConfigureAwait(false);
        }
        finally { response.Close(); }
    }

    private static int GetFreeLoopbackPort()
    {
        // TcpListener with port 0 lets the OS hand us a free port; we then close and reuse it
        // for the HttpListener. Tiny TOCTOU window, but the port is rebound within microseconds.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static (string verifier, string challenge) GeneratePkcePair()
    {
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64Url(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return (verifier, Base64Url(challengeBytes));
    }

    private static string RandomBase64Url(int byteLen)
    {
        var buf = new byte[byteLen];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenBrowser(string url)
    {
        // ShellExecute via Process.Start is the only cross-version way that handles every default-
        // browser configuration (DefaultBrowser registry, Win11 user-app-defaults). UseShellExecute
        // must be true; without it Process.Start treats url as an executable path and throws.
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
