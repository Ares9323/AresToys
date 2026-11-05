using System.IO;

namespace AresToys.App.Services.Peek;

/// <summary>How Peek shows a file: decoded as an image (WIC), rendered as a web page, or not previewed.</summary>
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

    private static readonly Dictionary<string, PeekLanguage> CodeLanguages = BuildCodeLanguages();

    /// <summary>What text a file without a recognised extension (or none) is shown as, when it is shown at all.</summary>
    private static readonly PeekLanguage PlainText = new("plaintext", "Text");

    /// <summary>The preview kind of the file at <paramref name="path"/>, by extension. A file with no extension is
    /// text, except a dotfile such as <c>.gitignore</c> that is listed, which is looked up by its whole name.</summary>
    public static PeekPreviewKind For(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return PeekPreviewKind.Image;
        if (HtmlExtensions.Contains(extension)) return PeekPreviewKind.Html;
        if (MarkdownExtensions.Contains(extension)) return PeekPreviewKind.Markdown;
        if (CodeLanguages.ContainsKey(extension)) return PeekPreviewKind.Code;

        // Path.GetExtension(".gitignore") is ".gitignore" already; a file with no dot at all (LICENSE, Makefile) is text.
        return extension.Length == 0 ? PeekPreviewKind.Code : PeekPreviewKind.None;
    }

    /// <summary>The highlight.js language id and display name for a <see cref="PeekPreviewKind.Code"/> file.</summary>
    public static PeekLanguage LanguageFor(string path)
        => CodeLanguages.TryGetValue(Path.GetExtension(path), out var language) ? language : PlainText;

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

        Add(new PeekLanguage("plaintext", "Text"), ".txt", ".log");
        Add(new PeekLanguage("plaintext", "CSV"), ".csv");
        Add(new PeekLanguage("plaintext", "TSV"), ".tsv");
        Add(new PeekLanguage("json", "JSON"), ".json", ".jsonc");
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
