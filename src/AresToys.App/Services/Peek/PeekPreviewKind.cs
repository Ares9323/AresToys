using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AresToys.App.Services.Peek;

/// <summary>How Peek shows a file: decoded as an image (WIC), rendered as a web page, hosted from a Windows preview handler, or not previewed.</summary>
internal enum PeekPreviewKind
{
    /// <summary>A bitmap format WIC decodes (<see cref="PeekImageLoader"/>).</summary>
    Image,

    /// <summary>An HTML file, rendered by the web view with scripts off.</summary>
    Html,

    /// <summary>A Markdown file, converted to a page by <see cref="PeekWebContent"/>.</summary>
    Markdown,

    /// <summary>A text or source file, coloured by highlight.js with line numbers.</summary>
    Code,

    /// <summary>A video file, played with controls by the web view (<see cref="PeekWebContent.BuildVideo"/>).</summary>
    Video,

    /// <summary>An SVG file no preview handler is registered for, shown as a picture by the web view (<see cref="PeekWebContent.BuildSvg"/>).</summary>
    Svg,

    /// <summary>A preview handler registered in Windows hosts the file (<see cref="PeekShellPreview"/>).</summary>
    Shell,

    /// <summary>No preview: the window shows the file name only.</summary>
    None,
}

/// <summary>The one place that decides the preview kind of a file from its name, and which highlight.js
/// language and display name a code or text file gets.</summary>
internal static class PeekPreview
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".jxr",
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml",
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown",
    };

    // What the browser engine decodes depends on the codecs the runtime has; a file it cannot play shows its name only.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".mov", ".ogv", ".mkv", ".avi",
    };

    private static readonly Dictionary<string, PeekLanguage> CodeLanguages = BuildCodeLanguages();

    /// <summary>What text a file without a recognised extension (or none) is shown as, when it is shown at all.</summary>
    private static readonly PeekLanguage PlainText = new("plaintext", "Text");

    private const string SvgExtension = ".svg";

    private const string PreviewHandlerKeyPath = @"shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    /// <summary>How many bytes of a file of unknown type are read to tell text from binary.</summary>
    private const int SniffBytes = 4096;

    /// <summary>The preview handler CLSID per extension (null: none registered). The registry is read once per
    /// extension per process.</summary>
    private static readonly ConcurrentDictionary<string, Guid?> HandlerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Guid, bool> LocalServerCache = new();

    /// <summary>The preview kind of the file at <paramref name="path"/>. Own kinds go by extension (image, HTML,
    /// Markdown, code); then a file with no extension is text, a registered Windows preview handler hosts the
    /// file, and last the file content decides: text when the first bytes are text, else no preview. A dotfile such
    /// as <c>.gitignore</c> that is listed is looked up by its whole name.</summary>
    public static PeekPreviewKind For(string path)
    {
        // The user's "Show as" pick wins over every rule below. A pick that cannot be shown (no preview handler,
        // not a format the image loader decodes) gives the name only.
        if (PeekShowAs.PickFor(path) is { } pick && PeekShowAs.Resolve(pick) is { } forced)
        {
            return forced.Kind switch
            {
                PeekPreviewKind.Shell => PreviewHandlerFor(path) is not null ? PeekPreviewKind.Shell : PeekPreviewKind.None,
                PeekPreviewKind.Image => CanDecodeAsImage(path) ? PeekPreviewKind.Image : PeekPreviewKind.None,
                _ => forced.Kind,
            };
        }

        return AutoKindFor(path);
    }

    /// <summary>True when the extension of <paramref name="path"/> is a bitmap format the image loader decodes.</summary>
    public static bool CanDecodeAsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>The kind the built-in rules give the file, ignoring the user's "Show as" pick.</summary>
    public static PeekPreviewKind AutoKindFor(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return PeekPreviewKind.Image;
        if (HtmlExtensions.Contains(extension)) return PeekPreviewKind.Html;
        if (MarkdownExtensions.Contains(extension)) return PeekPreviewKind.Markdown;
        if (VideoExtensions.Contains(extension)) return PeekPreviewKind.Video;
        if (CodeLanguages.ContainsKey(extension)) return PeekPreviewKind.Code;

        // Path.GetExtension(".gitignore") is ".gitignore" already; a file with no dot at all (LICENSE, Makefile) is text.
        if (extension.Length == 0) return PeekPreviewKind.Code;
        if (PreviewHandlerFor(path) is not null) return PeekPreviewKind.Shell;
        if (SvgExtension.Equals(extension, StringComparison.OrdinalIgnoreCase)) return PeekPreviewKind.Svg;
        return LooksLikeText(path) ? PeekPreviewKind.Code : PeekPreviewKind.None;
    }

    /// <summary>The CLSID of the Windows preview handler registered for the extension of <paramref name="path"/>,
    /// looked up on the extension key then on its ProgID (read through the merged classes root, so a per user
    /// registration wins over a machine one), or null when there is none.</summary>
    public static Guid? PreviewHandlerFor(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length == 0 ? null : HandlerCache.GetOrAdd(extension, LookUpPreviewHandler);
    }

    /// <summary>True when the handler <paramref name="clsid"/> is registered as its own local server (LocalServer32 in
    /// the 64 or the 32 bit registry view: the Office handlers), which a normal process can start. Every other handler
    /// is a DLL that Windows runs in a prevhost.exe surrogate, which only a Low integrity process can start
    /// (<see cref="PeekHandlerHost"/>). Cached per CLSID.</summary>
    public static bool RunsAsLocalServer(Guid clsid) => LocalServerCache.GetOrAdd(clsid, LookUpLocalServer);

    private static bool LookUpLocalServer(Guid clsid)
    {
        var subKey = $@"CLSID\{clsid:B}\LocalServer32";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var key = root.OpenSubKey(subKey);
            if (key is not null) return true;
        }

        return false;
    }

    private static Guid? LookUpPreviewHandler(string extension)
    {
        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        if (extensionKey is null) return null;

        if (ReadHandlerGuid(extensionKey) is { } direct) return direct;

        // Not on the extension: look on its file class (ProgID).
        if (extensionKey.GetValue(null) is string progId && progId.Length > 0)
        {
            using var classKey = Registry.ClassesRoot.OpenSubKey(progId);
            if (classKey is not null) return ReadHandlerGuid(classKey);
        }

        return null;
    }

    private static Guid? ReadHandlerGuid(RegistryKey key)
    {
        using var handlerKey = key.OpenSubKey(PreviewHandlerKeyPath);
        return handlerKey?.GetValue(null) is string text && Guid.TryParse(text, out var clsid) ? clsid : null;
    }

    /// <summary>True when the first bytes of the file are text: no NUL byte and valid UTF-8, or a UTF-16 byte order
    /// mark. An empty file counts as text. A file that cannot be read is not text.</summary>
    private static bool LooksLikeText(string path)
    {
        var buffer = new byte[SniffBytes];
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = stream.Read(buffer, 0, SniffBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
        {
            return true;
        }

        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return false;

        try
        {
            // A sequence cut by the 4096 byte limit is not an error: only the whole file is flushed.
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            decoder.GetCharCount(buffer, 0, read, read < SniffBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The highlight.js language id and display name for a <see cref="PeekPreviewKind.Code"/> file.</summary>
    public static PeekLanguage LanguageFor(string path)
        => PeekShowAs.PickFor(path) is { } pick && PeekShowAs.Resolve(pick) is { Language: { } picked }
            ? picked
            : AutoLanguageFor(path);

    /// <summary>The language the built-in rules give the file, ignoring the user's "Show as" pick.</summary>
    public static PeekLanguage AutoLanguageFor(string path)
        => CodeLanguages.TryGetValue(Path.GetExtension(path), out var language) ? language : PlainText;

    /// <summary>The languages of the "Show as" Code submenu: every known highlight.js language once, sorted by name.
    /// The ones that have their own menu entry (Text, JSON, XML, YAML, INI) are left out.</summary>
    public static IReadOnlyList<PeekLanguage> CodeLanguageChoices { get; } = CodeLanguages.Values
        .Where(l => l.Id is not ("plaintext" or "json" or "xml" or "yaml" or "ini"))
        .GroupBy(l => l.Id)
        .Select(g => g.First())
        .OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static Dictionary<string, PeekLanguage> BuildCodeLanguages()
    {
        var map = new Dictionary<string, PeekLanguage>(StringComparer.OrdinalIgnoreCase);
        void Add(PeekLanguage language, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                map[extension] = language;
            }
        }

        Add(new PeekLanguage("plaintext", "Text"), ".txt", ".log", ".nfo", ".info");
        Add(new PeekLanguage("plaintext", "CSV"), ".csv");
        Add(new PeekLanguage("plaintext", "TSV"), ".tsv");
        Add(new PeekLanguage("json", "JSON"), ".json", ".jsonc", ".uproject", ".uplugin", ".uprojectdirs", ".json5", ".jsonl", ".geojson", ".webmanifest", ".har");
        Add(new PeekLanguage("xml", "XML"), ".xml", ".xaml", ".csproj", ".props", ".targets", ".config");
        Add(new PeekLanguage("yaml", "YAML"), ".yaml", ".yml");
        Add(new PeekLanguage("ini", "TOML"), ".toml");
        Add(new PeekLanguage("ini", "INI"), ".ini", ".cfg", ".editorconfig", ".env");
        Add(new PeekLanguage("ini", "Registry"), ".reg");
        Add(new PeekLanguage("plaintext", "Git"), ".gitignore", ".gitattributes");
        Add(new PeekLanguage("diff", "Diff"), ".diff", ".patch");
        Add(new PeekLanguage("csharp", "C#"), ".cs");
        Add(new PeekLanguage("vbnet", "Visual Basic"), ".vb");
        Add(new PeekLanguage("fsharp", "F#"), ".fs");
        Add(new PeekLanguage("python", "Python"), ".py");
        Add(new PeekLanguage("javascript", "JavaScript"), ".js", ".mjs", ".jsx");
        Add(new PeekLanguage("typescript", "TypeScript"), ".ts", ".tsx");
        Add(new PeekLanguage("css", "CSS"), ".css");
        Add(new PeekLanguage("scss", "SCSS"), ".scss");
        Add(new PeekLanguage("less", "Less"), ".less");
        Add(new PeekLanguage("c", "C"), ".c", ".h");
        Add(new PeekLanguage("cpp", "C++"), ".cpp", ".hpp", ".cc");
        Add(new PeekLanguage("java", "Java"), ".java");
        Add(new PeekLanguage("kotlin", "Kotlin"), ".kt");
        Add(new PeekLanguage("go", "Go"), ".go");
        Add(new PeekLanguage("rust", "Rust"), ".rs");
        Add(new PeekLanguage("ruby", "Ruby"), ".rb");
        Add(new PeekLanguage("php", "PHP"), ".php");
        Add(new PeekLanguage("lua", "Lua"), ".lua");
        Add(new PeekLanguage("sql", "SQL"), ".sql");
        Add(new PeekLanguage("powershell", "PowerShell"), ".ps1", ".psm1");
        Add(new PeekLanguage("dos", "Batch"), ".bat", ".cmd");
        Add(new PeekLanguage("bash", "Shell"), ".sh", ".bash");
        return map;
    }
}

/// <summary>A highlight.js language id (the <c>language-X</c> class) and the name the footer shows.</summary>
internal readonly record struct PeekLanguage(string Id, string DisplayName);
