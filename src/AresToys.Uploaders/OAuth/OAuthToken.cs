using System.Text.Json.Serialization;

namespace AresToys.Uploaders.OAuth;

/// <summary>What we keep around between sessions. Serialized to JSON and stored as a single
/// sensitive value in <see cref="AresToys.PluginContracts.IPluginConfigStore"/> so DPAPI handles
/// the encryption — we don't roll our own. <see cref="ExpiresAt"/> is absolute UTC because
/// expires-in seconds aren't meaningful after the process restarts.</summary>
public sealed class OAuthToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Long-lived token used by <c>OAuthFlowService.RefreshAsync</c>. May be null
    /// when the provider doesn't issue one (e.g. user revoked offline access).</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>True with a 60-second safety window so callers can still use the returned token
    /// for the upload that immediately follows the check.</summary>
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(60);

    public static OAuthToken FromTokenResponse(System.Text.Json.JsonElement root)
    {
        var token = new OAuthToken
        {
            AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? string.Empty : string.Empty,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : null,
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null,
        };
        // expires_in is seconds-from-now per RFC 6749. Default to 1h when the provider doesn't
        // send it (Dropbox sometimes omits) so we still try to refresh proactively.
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
        token.ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn);
        return token;
    }
}
