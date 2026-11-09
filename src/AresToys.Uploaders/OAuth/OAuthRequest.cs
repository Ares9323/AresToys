namespace AresToys.Uploaders.OAuth;

/// <summary>Provider-agnostic description of an OAuth2 authorization-code flow. Every uploader
/// fills these in once (constants for endpoints + scope, plus the user-supplied client id /
/// optional secret) and hands the descriptor to <see cref="OAuthFlowService.AuthorizeAsync"/>;
/// the service handles loopback listener, browser launch, state validation, PKCE, and the token
/// exchange POST.</summary>
public sealed record OAuthAuthorizeRequest
{
    public required string AuthorizationEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string ClientId { get; init; }
    public required string Scope { get; init; }

    /// <summary>Confidential-client secret. Empty / null means a public client (PKCE-only).
    /// OneDrive / Google / Dropbox accept both modes; Imgur (user) uses the secret.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>S256 code challenge / code verifier. Recommended for every provider that supports
    /// it (OneDrive, Google, Dropbox); harmless for Imgur which ignores unknown params.</summary>
    public bool UsePkce { get; init; } = true;

    /// <summary>Extra query params appended to the authorization URL (e.g. <c>access_type=offline</c>
    /// for Google to force a refresh_token, <c>prompt=consent</c> to re-prompt). Token endpoint
    /// params are NOT extended via this hook — adjust in code for the rare service that needs it.</summary>
    public IReadOnlyDictionary<string, string>? ExtraAuthorizeParams { get; init; }

    /// <summary>Fixed loopback port to use for the redirect URI. Null (default) picks a random
    /// free port at runtime — works with providers that honour the OAuth "loopback rule" of
    /// matching any port when <c>http://localhost</c> is registered (Azure, Google).
    /// Providers that demand byte-exact <c>redirect_uri</c> matching (Dropbox is the notable
    /// one) need a fixed port both registered in their dashboard AND used at runtime —
    /// set this in the uploader's <c>BuildOAuthRequest</c> so the user can register
    /// <c>http://localhost:&lt;port&gt;/</c> once and we always reuse it.</summary>
    public int? LoopbackPort { get; init; }
}

/// <summary>Refresh-token grant. Same client identity as the original authorize. Uploader calls
/// this whenever <see cref="OAuthToken.IsExpired"/> on the stored token.</summary>
public sealed record OAuthRefreshRequest
{
    public required string TokenEndpoint { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string RefreshToken { get; init; }
}
