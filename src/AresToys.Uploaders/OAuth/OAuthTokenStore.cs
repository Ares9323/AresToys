using System.Text.Json;
using AresToys.PluginContracts;

namespace AresToys.Uploaders.OAuth;

/// <summary>Persists an <see cref="OAuthToken"/> as a single sensitive JSON value inside the
/// uploader's <see cref="IPluginConfigStore"/>. The store DPAPI-encrypts on write, so we don't
/// need to encrypt the JSON ourselves. Centralizes the "load + auto-refresh + save" cycle so
/// every uploader's <c>UploadAsync</c> can just call
/// <see cref="GetValidAccessTokenAsync"/> and trust the result.</summary>
public static class OAuthTokenStore
{
    /// <summary>Single key under which we store the serialized token. Per-uploader namespacing
    /// is handled by the host (<c>plugin.{uploaderId}.</c>), so two uploaders both using this
    /// "oauth_token" key won't collide.</summary>
    public const string Key = "oauth_token";

    public static async Task<OAuthToken?> LoadAsync(IPluginConfigStore store, CancellationToken cancellationToken)
    {
        var raw = await store.GetAsync(Key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonSerializer.Deserialize<OAuthToken>(raw); }
        catch (JsonException) { return null; }
    }

    public static Task SaveAsync(IPluginConfigStore store, OAuthToken token, CancellationToken cancellationToken)
        => store.SetAsync(Key, JsonSerializer.Serialize(token), sensitive: true, cancellationToken);

    public static Task ClearAsync(IPluginConfigStore store, CancellationToken cancellationToken)
        => store.DeleteAsync(Key, cancellationToken);

    /// <summary>Returns a valid access token, refreshing transparently when the stored one is
    /// expired. Throws <see cref="InvalidOperationException"/> when no token has been stored
    /// (uploader should surface "Not signed in") or when the refresh attempt fails (refresh
    /// token may have been revoked — caller should clear the token and re-prompt sign-in).
    /// Refresh response often omits a new refresh_token; in that case we keep the old one.</summary>
    public static async Task<string> GetValidAccessTokenAsync(
        IPluginConfigStore store,
        OAuthFlowService oauth,
        Func<string /*refreshToken*/, OAuthRefreshRequest> buildRefresh,
        CancellationToken cancellationToken)
    {
        var stored = await LoadAsync(store, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Not signed in.");

        if (!stored.IsExpired) return stored.AccessToken;
        if (string.IsNullOrEmpty(stored.RefreshToken))
            throw new InvalidOperationException("Access token expired and no refresh token available — sign in again.");

        var refreshed = await oauth.RefreshAsync(buildRefresh(stored.RefreshToken), cancellationToken).ConfigureAwait(false);
        // Providers that don't rotate the refresh_token leave it null in the response — keep the
        // existing one so the next refresh still works.
        if (string.IsNullOrEmpty(refreshed.RefreshToken)) refreshed.RefreshToken = stored.RefreshToken;
        await SaveAsync(store, refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed.AccessToken;
    }
}
