using System.Globalization;
using System.IO;
using System.Text;

namespace AresToys.App.Services.ImageResize;

/// <summary>File name helpers for resized output: token template, Windows-safe names and
/// collision avoidance.</summary>
public static class FileNameTemplate
{
    private static readonly char[] Invalid = { ':', '*', '?', '"', '<', '>', '|' };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Replaces {name} {preset} {w} {h} {ow} {oh} {format} {counter} from
    /// <paramref name="tokens"/> (keys without braces, case-insensitive). {date} and {time} are
    /// filled from the current time when not supplied. Unknown tokens are left as written.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var now = DateTime.Now;
        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    var key = template.Substring(i + 1, end - i - 1);
                    if (TryGet(tokens, key, out var value)) { sb.Append(value); i = end; continue; }
                    var lower = key.ToLowerInvariant();
                    if (lower == "date") { sb.Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); i = end; continue; }
                    if (lower == "time") { sb.Append(now.ToString("HH-mm-ss", CultureInfo.InvariantCulture)); i = end; continue; }
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> tokens, string key, out string value)
    {
        if (tokens.TryGetValue(key, out var v)) { value = v; return true; }
        foreach (var kv in tokens)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Replaces characters Windows rejects with '_' and appends '_' to a reserved
    /// device name. Path separators are replaced too, so the result is a single file name.</summary>
    public static string Sanitize(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "_";
        var chars = fileName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Invalid, chars[i]) >= 0 || chars[i] == '\\' || chars[i] == '/' || char.IsControl(chars[i]))
                chars[i] = '_';
        }
        var s = new string(chars);
        return Reserved.Contains(s) ? s + "_" : s;
    }

    /// <summary>Returns <paramref name="fullPath"/> if free, otherwise adds " (n)" before the
    /// extension with the first n from 1 that does not exist.</summary>
    public static string Uniquify(string fullPath)
    {
        if (!File.Exists(fullPath)) return fullPath;
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
