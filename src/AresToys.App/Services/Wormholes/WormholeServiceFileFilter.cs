using System.IO;
using System.Linq;

namespace AresToys.App.Services.Wormholes;

/// <summary>Hides the bookkeeping files the OS drops into folders. A wormhole shows one tile per
/// entry in a small window, so a <c>desktop.ini</c> Windows wrote to remember a folder's icon, or
/// a <c>Thumbs.db</c> it filled with thumbnail data, costs a tile the user would rather spend on
/// their own files. Explorer hides these by default (they're flagged hidden + system); a wormhole
/// enumerates the folder directly, so it has to do the same on purpose.
///
/// Matching is by exact filename rather than by file attributes: it stays predictable, and it
/// never swallows a file the user deliberately marked hidden and does want to see in there.</summary>
public static class WormholeServiceFileFilter
{
    /// <summary>The names, matched whole and case-insensitively. <c>desktop.ini</c> (folder view
    /// settings) and <c>Thumbs.db</c> / <c>ehthumbs.db</c> (thumbnail caches) are Windows';
    /// <c>.DS_Store</c> arrives on any folder that has passed through a Mac, which is common on
    /// shared drives.</summary>
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "Thumbs.db",
        "ehthumbs.db",
        ".DS_Store",
    };

    /// <summary>True when this entry is one of the OS's own service files and should stay out of
    /// the wormhole. Null / empty paths and paths with no filename component are not service
    /// files, so the caller's normal handling applies.</summary>
    public static bool IsServiceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string name;
        try
        {
            // Trim trailing separators first: GetFileName returns "" for "C:\W\Thumbs.db\".
            name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return false;
        }
        return name.Length > 0 && Names.Contains(name);
    }

    /// <summary>Drop every service file from an enumeration.</summary>
    public static IEnumerable<string> Exclude(IEnumerable<string> entries) =>
        entries.Where(e => !IsServiceFile(e));

    /// <summary>Conditional form for call sites holding the user's preference: returns the
    /// sequence untouched when <paramref name="hide"/> is false, so the caller stays a one-liner
    /// and the preference is read in exactly one place.</summary>
    public static IEnumerable<string> ExcludeIf(IEnumerable<string> entries, bool hide) =>
        hide ? Exclude(entries) : entries;
}
