using System.IO;

namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Read/write helper for Windows Internet Shortcut (<c>.url</c>) files. These are plain
/// INI: a <c>[InternetShortcut]</c> section with <c>URL=</c>, optionally <c>IconFile=</c> /
/// <c>IconIndex=</c>. We only ever touch the icon keys (favicon write-through) and read the URL
/// — everything else (HotKey, IDList, custom sections written by browsers) is preserved verbatim.
///
/// The line-level transforms are pure and side-effect free so they unit-test without disk; the
/// file-level wrappers are thin IO over them.</summary>
public static class UrlShortcutFile
{
    private const string Section = "[InternetShortcut]";

    /// <summary>Return the <c>URL=</c> value, or null if absent / unreadable. Looks across the
    /// whole file (browsers occasionally emit the key before the section header), case-insensitive
    /// on the key.</summary>
    public static string? ReadUrl(string filePath)
    {
        try { return ExtractValue(File.ReadAllLines(filePath), "URL"); }
        catch { return null; }
    }

    /// <summary>Return the current <c>IconFile=</c> value, or null if absent / unreadable.</summary>
    public static string? ReadIconFile(string filePath)
    {
        try { return ExtractValue(File.ReadAllLines(filePath), "IconFile"); }
        catch { return null; }
    }

    /// <summary>Write (or replace) <c>IconFile=</c> + <c>IconIndex=</c> inside the
    /// <c>[InternetShortcut]</c> section, preserving every other line. Returns false on IO
    /// failure (e.g. read-only file) so the caller can fall back to in-app rendering.</summary>
    public static bool SetIcon(string filePath, string iconFilePath, int iconIndex)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var updated = SetIconLines(lines, iconFilePath, iconIndex);
            File.WriteAllLines(filePath, updated);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pure transform: return a copy of <paramref name="lines"/> with <c>IconFile</c>
    /// and <c>IconIndex</c> set to the given values inside <c>[InternetShortcut]</c>. Existing
    /// occurrences are replaced in place; missing ones are appended to the section. If the
    /// section is absent it's created at the end. Comments, blank lines, and unrelated keys are
    /// left untouched.</summary>
    public static IReadOnlyList<string> SetIconLines(IReadOnlyList<string> lines, string iconFilePath, int iconIndex)
    {
        var result = new List<string>(lines.Count + 3);
        var inTargetSection = false;
        var sectionSeen = false;
        var wroteIconFile = false;
        var wroteIconIndex = false;

        void FlushPendingIconKeys()
        {
            // Called right before we leave the target section (or at EOF while still inside it):
            // append whichever icon keys we didn't already replace in place.
            if (!wroteIconFile) { result.Add($"IconFile={iconFilePath}"); wroteIconFile = true; }
            if (!wroteIconIndex) { result.Add($"IconIndex={iconIndex}"); wroteIconIndex = true; }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('['))
            {
                // Entering a new section. If we were inside the target one, flush any missing keys
                // before the new header so they stay within [InternetShortcut].
                if (inTargetSection) FlushPendingIconKeys();
                inTargetSection = trimmed.TrimEnd().Equals(Section, StringComparison.OrdinalIgnoreCase);
                if (inTargetSection) sectionSeen = true;
                result.Add(line);
                continue;
            }

            if (inTargetSection)
            {
                var key = KeyOf(line);
                if (string.Equals(key, "IconFile", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add($"IconFile={iconFilePath}");
                    wroteIconFile = true;
                    continue;
                }
                if (string.Equals(key, "IconIndex", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add($"IconIndex={iconIndex}");
                    wroteIconIndex = true;
                    continue;
                }
            }

            result.Add(line);
        }

        if (inTargetSection)
        {
            // File ended while still inside [InternetShortcut].
            FlushPendingIconKeys();
        }
        else if (!sectionSeen)
        {
            // No [InternetShortcut] anywhere — create it. (Unusual for a real .url, but keeps the
            // transform total rather than silently no-op.)
            result.Add(Section);
            result.Add($"IconFile={iconFilePath}");
            result.Add($"IconIndex={iconIndex}");
        }

        return result;
    }

    /// <summary>Extract the value of <paramref name="wantedKey"/> (case-insensitive) from INI
    /// lines, ignoring section headers. Returns the first match, trimmed, or null.</summary>
    public static string? ExtractValue(IReadOnlyList<string> lines, string wantedKey)
    {
        foreach (var line in lines)
        {
            var key = KeyOf(line);
            if (key is null) continue;
            if (string.Equals(key, wantedKey, StringComparison.OrdinalIgnoreCase))
            {
                var eq = line.IndexOf('=');
                return line[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    /// <summary>The key part (left of the first '=') of an INI line, trimmed, or null when the
    /// line isn't a <c>key=value</c> pair (blank, comment, or section header).</summary>
    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '[' || trimmed[0] == ';') return null;
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;
        return line[..eq].Trim();
    }
}
