using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AresToys.App.Services.NewPlus;

/// <summary>The template rules of New+, ported from PowerToys: which entries of the template folder
/// are templates, the name shown in the menu, the name of the created file, and the variables
/// (<c>$YYYY</c>, <c>%ENV%</c>, <c>$PARENT_FOLDER_NAME</c>) a name can carry. Pure functions over
/// strings and the file system, no registry and no UI.</summary>
internal static class NewPlusTemplates
{
    public const string ParentFolderVariable = "$PARENT_FOLDER_NAME";

    private static readonly System.Buffers.SearchValues<char> InvalidNameChars =
        System.Buffers.SearchValues.Create("/\\:*?\"<>|");

    /// <summary>The templates in <paramref name="folder"/>: folders first, then files, each group in
    /// natural order. Hidden and system files are skipped (folders are never skipped, like
    /// PowerToys). A missing folder is no templates.</summary>
    public static IReadOnlyList<string> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            var dirs = Directory.EnumerateDirectories(folder).ToList();
            var files = Directory.EnumerateFiles(folder)
                .Where(f => !IsHiddenOrSystem(f))
                .ToList();
            dirs.Sort(NaturalStringComparer.Instance);
            files.Sort(NaturalStringComparer.Instance);
            return [.. dirs, .. files];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Explorer passes the folder as <c>"%V"</c>; for a drive root that is <c>"C:\"</c> and the
    /// backslash escapes the closing quote, so the argument reaches us as <c>C:\"</c>.</summary>
    public static string RepairQuotedFolderArg(string arg)
    {
        var folder = arg.TrimEnd('"');
        return folder.Length == 2 && folder[1] == ':' ? folder + '\\' : folder;
    }

    public static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch { return false; }
    }

    /// <summary>The text the menu shows for a template.</summary>
    public static string MenuTitle(string templatePath, NewPlusSettings settings, DateTime now)
    {
        var title = Path.GetFileName(templatePath);
        if (settings.HideStartingDigits) title = RemoveStartingDigits(title, Directory.Exists(templatePath));
        if (settings.ReplaceVariables) title = ResolveFileName(title, parentFolderName: string.Empty, now);

        if (!settings.HideFileExtension || Directory.Exists(templatePath)) return title;
        var ext = Path.GetExtension(templatePath);
        return ext.Length > 0 && title.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            ? title[..^ext.Length]
            : title;
    }

    /// <summary>The name a template gets in the folder it is created in, before variables.</summary>
    public static string TargetName(string templatePath, bool hideStartingDigits)
    {
        var name = Path.GetFileName(templatePath);
        return hideStartingDigits ? RemoveStartingDigits(name, Directory.Exists(templatePath)) : name;
    }

    /// <summary>Drops the number a template is sorted by: "01. First.txt" becomes "First.txt".
    /// A name that is only digits is kept (a file always, a folder unless the dot starts an
    /// extension, as in "0123.Name").</summary>
    public static string RemoveStartingDigits(string filename, bool isDirectory)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.Length > 0 && stem.All(c => c is >= '0' and <= '9'))
        {
            var hasExtension = Path.HasExtension(filename);
            if (!isDirectory || !hasExtension) return filename;
        }

        var digitsEnd = 0;
        while (digitsEnd < filename.Length && filename[digitsEnd] is >= '0' and <= '9') digitsEnd++;
        if (digitsEnd == 0) return filename;

        var strip = digitsEnd;
        if (strip < filename.Length)
        {
            if (filename[strip] == '.')
            {
                strip++;
                if (strip < filename.Length && filename[strip] == ' ') strip++;
            }
            else if (filename[strip] == ' ')
            {
                strip++;
                if (strip < filename.Length && filename[strip] == '.') strip++;
            }
        }
        return filename[strip..];
    }

    /// <summary>Resolves the variables of one file or folder name: date and time, then
    /// <c>%ENV%</c>, then <c>$PARENT_FOLDER_NAME</c> (when a parent name is known), then
    /// characters Windows forbids in names become spaces.</summary>
    public static string ResolveFileName(string name, string parentFolderName, DateTime now)
    {
        var result = ResolveDateTime(name, now);
        result = ResolveEnvironment(result);
        if (parentFolderName.Length > 0)
            result = result.Replace(ParentFolderVariable, parentFolderName, StringComparison.Ordinal);
        return MakeValidFileName(result);
    }

    public static string MakeValidFileName(string name)
    {
        if (name.AsSpan().IndexOfAny(InvalidNameChars) < 0) return name;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (InvalidNameChars.Contains(chars[i])) chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>Case insensitive <c>%NAME%</c> replacement, like cmd. An unknown variable stays as
    /// typed.</summary>
    public static string ResolveEnvironment(string text)
        => Regex.Replace(text, "%([^%]+)%", m =>
        {
            var value = Environment.GetEnvironmentVariable(m.Groups[1].Value);
            return string.IsNullOrEmpty(value) ? m.Value : value;
        });

    // Longest token first, so "$YYYY" is not eaten as "$Y" followed by "YYY". Case matters: "$HH"
    // is the 12 hour clock and "$hh" the 24 hour one, exactly as in PowerToys.
    private static readonly (string Token, Func<DateTime, CultureInfo, string> Value)[] DateTokens =
    [
        ("YYYY", (t, _) => t.Year.ToString("D4", CultureInfo.InvariantCulture)),
        ("YY", (t, _) => (t.Year % 100).ToString("D2", CultureInfo.InvariantCulture)),
        ("Y", (t, _) => (t.Year % 10).ToString(CultureInfo.InvariantCulture)),
        ("MMMM", (t, c) => Capitalize(t.ToString("MMMM", c))),
        ("MMM", (t, c) => Capitalize(t.ToString("MMM", c))),
        ("MM", (t, _) => t.Month.ToString("D2", CultureInfo.InvariantCulture)),
        ("M", (t, _) => t.Month.ToString(CultureInfo.InvariantCulture)),
        ("DDDD", (t, c) => Capitalize(t.ToString("dddd", c))),
        ("DDD", (t, c) => Capitalize(t.ToString("ddd", c))),
        ("DD", (t, _) => t.Day.ToString("D2", CultureInfo.InvariantCulture)),
        ("D", (t, _) => t.Day.ToString(CultureInfo.InvariantCulture)),
        ("HH", (t, _) => Hour12(t).ToString("D2", CultureInfo.InvariantCulture)),
        ("H", (t, _) => Hour12(t).ToString(CultureInfo.InvariantCulture)),
        ("TT", (t, _) => t.Hour < 12 ? "AM" : "PM"),
        ("tt", (t, _) => t.Hour < 12 ? "am" : "pm"),
        ("hh", (t, _) => t.Hour.ToString("D2", CultureInfo.InvariantCulture)),
        ("h", (t, _) => t.Hour.ToString(CultureInfo.InvariantCulture)),
        ("mm", (t, _) => t.Minute.ToString("D2", CultureInfo.InvariantCulture)),
        ("m", (t, _) => t.Minute.ToString(CultureInfo.InvariantCulture)),
        ("ss", (t, _) => t.Second.ToString("D2", CultureInfo.InvariantCulture)),
        ("s", (t, _) => t.Second.ToString(CultureInfo.InvariantCulture)),
        ("fff", (t, _) => t.Millisecond.ToString("D3", CultureInfo.InvariantCulture)),
        ("ff", (t, _) => (t.Millisecond / 10).ToString("D2", CultureInfo.InvariantCulture)),
        ("f", (t, _) => (t.Millisecond / 100).ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>Replaces <c>$YYYY</c>, <c>$MM</c>, <c>$hh</c> and the rest. A <c>$$</c> pair in front
    /// of a token escapes it, so "$$YYYY" stays as typed.</summary>
    public static string ResolveDateTime(string text, DateTime now)
    {
        if (!text.Contains('$')) return text;
        var culture = CultureInfo.CurrentCulture;
        var result = text;
        foreach (var (token, value) in DateTokens)
        {
            var replacement = value(now, culture);
            result = Regex.Replace(result, @"(([^$]|^)(\$\$)*)\$" + token,
                m => m.Groups[1].Value + replacement);
        }
        return result;
    }

    /// <summary>"name.ext" becomes "name (1).ext", "name (2).ext" and so on until nothing exists
    /// at that path.</summary>
    public static string MakeUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var counter = 1; ; counter++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({counter}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static int Hour12(DateTime t) => t.Hour % 12 == 0 ? 12 : t.Hour % 12;

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
