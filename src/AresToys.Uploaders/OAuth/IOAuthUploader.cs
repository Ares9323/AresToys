namespace AresToys.Uploaders.OAuth;

/// <summary>Implemented by uploaders that authenticate via OAuth2 (OneDrive, Google Drive,
/// Dropbox, Imgur user-mode, …). Parallel to <c>IConfigurableUploader</c>: a single uploader
/// usually implements both — OAuth handles the sign-in side, IConfigurableUploader exposes
/// per-account preferences (target folder, public toggle, …). The host's settings dialog
/// reads this interface to render the sign-in panel and drives the flow through
/// <see cref="OAuthFlowService"/>.</summary>
public interface IOAuthUploader
{
    /// <summary>The provider-specific OAuth descriptor — endpoints, scope, client id/secret,
    /// PKCE flag. Built fresh on every sign-in attempt because the client id/secret may have
    /// just been edited in the same dialog.</summary>
    OAuthAuthorizeRequest BuildOAuthRequest();

    /// <summary>Same descriptor as <see cref="BuildOAuthRequest"/> but for the refresh grant.
    /// Returned separately because refresh doesn't carry scope / redirect_uri / PKCE — and the
    /// uploader is the one that knows whether the provider needs a client_secret on refresh.</summary>
    OAuthRefreshRequest BuildRefreshRequest(string refreshToken);

    /// <summary>Optional account label shown in the dialog ("Signed in as foo@example.com").
    /// Default returns null and the dialog falls back to a plain "Signed in". Override to call
    /// the provider's userinfo endpoint (e.g. Graph <c>/me</c>, Google <c>userinfo.email</c>,
    /// Dropbox <c>users/get_current_account</c>) when you want the nicer label.</summary>
    Task<string?> GetSignedInDisplayNameAsync(string accessToken, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
