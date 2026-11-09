namespace AresToys.Uploaders;

/// <summary>Bundled OAuth client credentials for the built-in uploaders. This file ships with
/// every constant set to the empty string — fresh clones / CI builds compile cleanly but no
/// uploader will sign-in until the user supplies their own credentials in the Settings dialog.
///
/// Maintainers ship a real build by creating <c>Secrets.Local.cs</c> alongside this file with
/// the same class and the same constants but real values. The <c>.csproj</c> uses a
/// <c>&lt;Compile Remove="Secrets.cs" Condition="Exists('Secrets.Local.cs')" /&gt;</c> so the
/// Local copy wins when present and end-users get zero-friction sign-in. <c>Secrets.Local.cs</c>
/// is gitignored — the real keys never enter the public repo.
///
/// Why <c>internal</c>: the constants are only consumed by uploaders inside this assembly. If
/// they leaked to a downstream package they'd embed our keys into anyone's redistribution.</summary>
internal static class Secrets
{
    // ---- OneDrive (Azure AD v2) ----
    // Public client + PKCE — Client Secret is optional (and discouraged for desktop apps).
    public const string OneDriveClientId = "";
    public const string OneDriveClientSecret = "";

    // ---- Google Drive ----
    // Desktop OAuth client. Google docs explicitly say the client_secret is "not actually secret"
    // for installed apps, so embedding it here is sanctioned.
    public const string GoogleDriveClientId = "";
    public const string GoogleDriveClientSecret = "";

    // ---- Dropbox ----
    // Public client with PKCE — Client Secret optional.
    public const string DropboxClientId = "";
    public const string DropboxClientSecret = "";

    // ---- Imgur ----
    // Anonymous uploads only — uses Authorization: Client-ID. No OAuth user-mode (Imgur's
    // app registration page is broken / inaccessible, see r/learnprogramming thread linked
    // in the AresToys design notes).
    public const string ImgurClientId = "";
}
