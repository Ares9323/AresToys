using System.IO;
using System.Text;

namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Disk store for downloaded favicons, keyed by host so all links to the same site
/// share one file (30 GitHub links → a single <c>github.com.ico</c>). Default location is
/// <c>%LocalAppData%\AresToys\favicons</c>; a root override is accepted for tests. Owns the
/// hygiene policy via <see cref="Purge"/>: drop icons for hosts no longer referenced by any live
/// link, and drop icons past a max age so they re-download lazily (logo refresh).</summary>
public sealed class FaviconCache
{
    private readonly string _dir;

    public FaviconCache(string? rootOverride = null)
    {
        _dir = rootOverride
               ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AresToys", "favicons");
    }

    public string DirectoryPath => _dir;

    /// <summary>Absolute path of the <c>.ico</c> for <paramref name="host"/> (whether or not it
    /// exists yet). Host is sanitized to a safe, lowercase filename.</summary>
    public string PathForHost(string host) => Path.Combine(_dir, SanitizeHost(host) + ".ico");

    /// <summary>True when a cached <c>.ico</c> already exists for the host.</summary>
    public bool Has(string host) => File.Exists(PathForHost(host));

    /// <summary>Write the icon bytes for a host, creating the cache directory on first use.
    /// Best-effort: returns false on IO failure rather than throwing into the caller's async void.</summary>
    public bool Save(string host, byte[] icoBytes)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(PathForHost(host), icoBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Delete cached icons that are either (a) for a host not in <paramref name="liveHosts"/>
    /// — links the user removed — or (b) older than <paramref name="maxAge"/> relative to
    /// <paramref name="utcNow"/>, so a stale logo re-downloads on next render. <paramref name="utcNow"/>
    /// is passed in for deterministic tests. No-ops cleanly when the directory doesn't exist.</summary>
    public void Purge(IReadOnlyCollection<string> liveHosts, TimeSpan maxAge, DateTime utcNow)
    {
        if (!Directory.Exists(_dir)) return;

        // Sanitize the live set once so the comparison matches the on-disk filenames exactly.
        var liveFileStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in liveHosts) liveFileStems.Add(SanitizeHost(h));

        foreach (var file in Directory.EnumerateFiles(_dir, "*.ico"))
        {
            try
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var unreferenced = !liveFileStems.Contains(stem);
                var stale = (utcNow - File.GetLastWriteTimeUtc(file)) > maxAge;
                if (unreferenced || stale) File.Delete(file);
            }
            catch
            {
                // A locked / vanished file shouldn't abort the sweep — skip and continue.
            }
        }
    }

    /// <summary>Lowercase the host and replace any character that isn't a safe filename char
    /// (keep <c>a-z 0-9 . -</c>) with '_'. Real hosts are already filename-safe (punycode for
    /// IDN is ASCII); this is belt-and-suspenders against odd input.</summary>
    public static string SanitizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "_";
        var sb = new StringBuilder(host.Length);
        foreach (var ch in host.Trim().ToLowerInvariant())
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '-';
            sb.Append(ok ? ch : '_');
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
