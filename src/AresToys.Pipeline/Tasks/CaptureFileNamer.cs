using System.Text;
using System.Text.RegularExpressions;
using AresToys.Storage.Settings;

namespace AresToys.Pipeline.Tasks;

/// <summary>Builds the base file name (no extension) of every saved capture: screenshots,
/// SVG traces, pinned-image saves and screen recordings. Two user settings drive it:
/// <list type="bullet">
/// <item><c>capture.file_prefix</c>: free text put in front, default <c>arestoys</c>. An empty
/// string means "no prefix"; a missing key means the default.</item>
/// <item><c>capture.file_name_pattern</c>: the ShareX-style date tokens of
/// <see cref="DatePatternExpander"/> plus <c>%title</c> (window title), <c>%appName</c>
/// (process name of the captured app) and <c>%ms</c> (milliseconds). Default
/// <see cref="DefaultPattern"/>: <c>arestoys-yyyyMMdd-HHmmss_&lt;app&gt;</c>, date first so
/// files sort chronologically in Explorer. Two captures in
/// the same second don't overwrite each other: the save tasks append <c>-1</c>, <c>-2</c>, ….</item>
/// </list>
/// Prefix and expanded pattern are joined with <c>-</c>. Separators left dangling by an empty
/// token (no window title, unknown app) are collapsed so names never read <c>arestoys--2026…</c>.</summary>
public static partial class CaptureFileNamer
{
    public const string PrefixSettingKey = "capture.file_prefix";
    public const string PatternSettingKey = "capture.file_name_pattern";
    public const string DefaultPrefix = "arestoys";
    public const string DefaultPattern = "%y%mo%d-%h%mi%s_%appName";

    /// <summary>Read both settings and build the name for "now".</summary>
    public static async Task<string> BuildAsync(ISettingsStore settings, string? windowTitle, string? appName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var prefix = await settings.GetAsync(PrefixSettingKey, cancellationToken).ConfigureAwait(false);
        var pattern = await settings.GetAsync(PatternSettingKey, cancellationToken).ConfigureAwait(false);
        return Build(prefix, pattern, DateTime.Now, windowTitle, appName);
    }

    /// <summary>Pure core of <see cref="BuildAsync"/>. <paramref name="prefix"/> /
    /// <paramref name="pattern"/> null = default; a blank pattern also falls back to the default
    /// (a file name made of the prefix alone would collide on every capture).</summary>
    public static string Build(string? prefix, string? pattern, DateTime now, string? windowTitle, string? appName)
    {
        prefix ??= DefaultPrefix;
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;

        // Dates first, then the free-text tokens: a window title containing "%d" must stay
        // literal instead of being expanded as a date token.
        var expanded = DatePatternExpander.Expand(pattern, now)
            .Replace("%title", Slug(windowTitle), StringComparison.OrdinalIgnoreCase)
            .Replace("%appName", Slug(appName), StringComparison.OrdinalIgnoreCase);

        var name = Sanitize(prefix.Trim()) is { Length: > 0 } p ? $"{p}-{Sanitize(expanded)}" : Sanitize(expanded);
        name = RepeatedDashes().Replace(name, "-").Trim('-', '_', ' ', '.');
        return name.Length > 0 ? name : DefaultPrefix;
    }

    /// <summary>Window titles and process names: invalid chars, dashes and spaces become
    /// <c>_</c> (so they read as one chunk between the pattern's own dashes), capped at 40
    /// chars. Same rule the save tasks used before this class existed.</summary>
    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '-' || c == ' ') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim('_');
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>Drop characters Windows refuses in a file name (the user may type any of them
    /// in the prefix or pattern boxes, and <c>%</c> tokens that didn't match stay literal).</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        return sb.ToString();
    }

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashes();
}
