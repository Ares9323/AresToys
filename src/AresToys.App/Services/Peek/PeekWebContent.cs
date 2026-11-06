using System.Globalization;
using System.IO;
using System.Text;
using Markdig;
using AresStrings = AresToys.App.Resources.Strings;

namespace AresToys.App.Services.Peek;

/// <summary>The colours a generated Peek page takes from the app theme: dark or light highlight.js style, and the
/// page background and text as CSS colours (<c>#RRGGBB</c>).</summary>
internal sealed record PeekPageTheme(bool Dark, string Background, string Foreground);

/// <summary>A generated page for the web view and the footer text that goes with it.</summary>
internal sealed record PeekWebPage(string Html, string Info);

/// <summary>Builds the pages the Peek web view shows for Markdown and for text and source files, and the footer
/// text of every web preview. The pages load highlight.js and its styles from the virtual host
/// <see cref="AssetsHost"/> (the <c>Web</c> folder next to the app) and resolve relative links against
/// <see cref="FileHost"/> (the folder of the file shown). Pure: no UI access, safe on a thread-pool thread.</summary>
internal static class PeekWebContent
{
    /// <summary>The virtual host that serves highlight.js and its styles.</summary>
    public const string AssetsHost = "peek-assets.arestoys";

    /// <summary>The virtual host that serves the folder of the file being previewed.</summary>
    public const string FileHost = "peek-file.arestoys";

    /// <summary>At most this many bytes of a file are read and shown.</summary>
    public const int MaxBytes = 1024 * 1024;

    // NavigateToString refuses more than 2 MiB of HTML. The page chrome is a few KB; what the file adds (its text
    // escaped, or the Markdown converted) is kept under this, cutting the text further when it does not fit.
    private const int ContentBudgetBytes = 1_800_000;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static PeekWebContent()
    {
        // Windows-1252 is the fallback for text that is not valid UTF-8; .NET needs its code page provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Builds the page for a <see cref="PeekPreviewKind.Markdown"/> or <see cref="PeekPreviewKind.Code"/>
    /// file. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file cannot be read
    /// and <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    public static PeekWebPage Build(string path, PeekPreviewKind kind, PeekPageTheme theme, CancellationToken ct)
    {
        var read = ReadText(path, ct);
        ct.ThrowIfCancellationRequested();
        return kind == PeekPreviewKind.Markdown ? BuildMarkdown(read, theme) : BuildCode(path, read, theme);
    }

    /// <summary>The footer text of an HTML preview: the file type and size.</summary>
    public static string DescribeHtml(string path)
    {
        try
        {
            return "HTML · " + FormatBytes(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable: the footer omits the size.
            return "HTML";
        }
    }

    /// <summary>The page for a <see cref="PeekPreviewKind.Video"/> file: one <c>video</c> element with controls that
    /// starts playing, reading the file from <see cref="FileHost"/>. The page posts <c>ok|width|height|seconds|version</c>
    /// once the video's metadata is known and <c>err|version</c> when the engine cannot play it; Space does nothing in
    /// the page (it is the key that closes Peek). The footer text is the type and size.</summary>
    public static PeekWebPage BuildVideo(string path, PeekPageTheme theme, int version)
    {
        var info = DescribeVideo(path);
        var v = version.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(2048);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; media-src https://")
          .Append(FileHost).Append("; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">");
        sb.Append("<style>:root{--bg:").Append(theme.Background).Append(";color-scheme:").Append(theme.Dark ? "dark" : "light")
          .Append("}html,body{margin:0;height:100%;overflow:hidden;background:var(--bg)}")
          .Append("video{display:block;width:100%;height:100%;object-fit:contain;outline:none}</style></head><body>");
        sb.Append("<video id=\"v\" controls autoplay tabindex=\"-1\" disablepictureinpicture src=\"https://").Append(FileHost)
          .Append('/').Append(Uri.EscapeDataString(Path.GetFileName(path))).Append("\"></video><script>");
        sb.Append("var v=document.getElementById('v');var n=").Append(v).Append(';')
          .Append("function post(s){window.chrome.webview.postMessage(s);}")
          .Append("document.addEventListener('keydown',function(e){if(e.code==='Space'||e.key===' ')e.preventDefault();},true);")
          .Append("v.addEventListener('keyup',function(e){if(e.code==='Space'||e.key===' ')e.preventDefault();},true);")
          .Append("v.addEventListener('loadedmetadata',function(){if(!v.videoWidth){post('err|'+n);return;}")
          .Append("post('ok|'+v.videoWidth+'|'+v.videoHeight+'|'+v.duration+'|'+n);});")
          .Append("v.addEventListener('error',function(){post('err|'+n);});")
          .Append("var p=v.play();if(p&&p.catch)p.catch(function(){});");
        sb.Append("</script></body></html>");
        return new PeekWebPage(sb.ToString(), info);
    }

    /// <summary>The page for a <see cref="PeekPreviewKind.Svg"/> file: the drawing in an <c>img</c> (so scripts inside
    /// it never run), read from <see cref="FileHost"/>, fitted and centred on the page background.</summary>
    public static PeekWebPage BuildSvg(string path, PeekPageTheme theme)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https://")
          .Append(FileHost).Append("; style-src 'unsafe-inline'\">");
        sb.Append("<style>:root{--bg:").Append(theme.Background).Append(";color-scheme:").Append(theme.Dark ? "dark" : "light")
          .Append("}html,body{margin:0;height:100%;overflow:hidden;background:var(--bg)}")
          .Append("body{display:flex;align-items:center;justify-content:center}")
          .Append("img{max-width:100%;max-height:100%;object-fit:contain}</style></head><body>");
        sb.Append("<img src=\"https://").Append(FileHost).Append('/').Append(Uri.EscapeDataString(Path.GetFileName(path)))
          .Append("?v=").Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)).Append("\" alt=\"\">");
        sb.Append("</body></html>");
        string info;
        try
        {
            info = "SVG  ·  " + FormatBytes(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable: the footer omits the size.
            info = "SVG";
        }

        return new PeekWebPage(sb.ToString(), info);
    }

    /// <summary>The footer text of a video before its metadata is known: the extension and the size.</summary>
    public static string DescribeVideo(string path)
    {
        var type = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try
        {
            return type + "  ·  " + FormatBytes(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable: the footer omits the size.
            return type;
        }
    }

    /// <summary>The footer text of a video once the page reported its metadata.</summary>
    public static string DescribeVideo(string baseInfo, int width, int height, double seconds)
    {
        var text = baseInfo + "  ·  " + string.Create(CultureInfo.CurrentCulture, $"{width}×{height}");
        if (double.IsFinite(seconds) && seconds >= 0)
        {
            var total = (long)Math.Round(seconds);
            var span = TimeSpan.FromSeconds(total);
            text += "  ·  " + (span.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}"));
        }

        return text;
    }

    /// <summary>Bytes as B, KB, MB or GB (1024 steps), one decimal in the current culture, none for plain bytes.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = unit == 0
            ? ((long)value).ToString(CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
        return number + " " + units[unit];
    }

    private static string FormatResource(string format, object argument)
        => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), argument);

    private sealed record TextRead(string Text, string EncodingName, Encoding Encoding, long FileBytes, long ReadBytes, bool Truncated);

    private static TextRead ReadText(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var length = stream.Length;
        var count = (int)Math.Min(length, MaxBytes);
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, filled, count - filled);
            if (read == 0)
            {
                break; // The file shrank while it was read.
            }

            filled += read;
        }

        var truncated = length > MaxBytes;
        var (encoding, name, bom) = DetectBom(buffer, filled);
        if (encoding is null)
        {
            try
            {
                var text = Decode(StrictUtf8, buffer, 0, filled, flush: !truncated);
                return new TextRead(text, "UTF-8", StrictUtf8, length, filled, truncated);
            }
            catch (DecoderFallbackException)
            {
                // Not valid UTF-8: legacy text, read as Windows-1252.
                var latin = Encoding.GetEncoding(1252);
                return new TextRead(Decode(latin, buffer, 0, filled, flush: true), "Windows-1252", latin, length, filled, truncated);
            }
        }

        return new TextRead(Decode(encoding, buffer, bom, filled - bom, flush: !truncated), name, encoding, length, filled, truncated);
    }

    /// <summary>The encoding a byte order mark announces, its display name and its length; a null encoding when there is none.</summary>
    private static (Encoding? Encoding, string Name, int BomLength) DetectBom(byte[] b, int n)
    {
        if (n >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return (new UTF32Encoding(false, true), "UTF-32 LE", 4);
        if (n >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return (new UTF32Encoding(true, true), "UTF-32 BE", 4);
        if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), "UTF-8", 3);
        if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true), "UTF-16 LE", 2);
        if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true), "UTF-16 BE", 2);
        return (null, string.Empty, 0);
    }

    /// <summary>Decodes with a fresh decoder. With <paramref name="flush"/> false an incomplete sequence at the end
    /// (a cut in the middle of a character) is held back instead of being an error.</summary>
    private static string Decode(Encoding encoding, byte[] bytes, int start, int count, bool flush)
    {
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(count)];
        var written = decoder.GetChars(bytes, start, count, chars, 0, flush);
        return new string(chars, 0, written);
    }

    private static PeekWebPage BuildCode(string path, TextRead read, PeekPageTheme theme)
    {
        var language = PeekPreview.LanguageFor(path);
        var escaped = new StringBuilder(Math.Min(read.Text.Length + read.Text.Length / 8, ContentBudgetBytes));
        var lines = 0;
        var consumed = 0;
        var bytes = 0;
        var previous = '\0';
        for (; consumed < read.Text.Length; consumed++)
        {
            var c = read.Text[consumed];
            var piece = c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => null,
            };
            var cost = piece?.Length ?? (c < 0x80 ? 1 : c < 0x800 ? 2 : 3);
            if (bytes + cost > ContentBudgetBytes)
            {
                break;
            }

            bytes += cost;
            if (piece is null) escaped.Append(c); else escaped.Append(piece);

            // The HTML parser reads CRLF, LF and a lone CR each as one line break; count the same way.
            if (c == '\r' || (c == '\n' && previous != '\r'))
            {
                lines++;
            }

            previous = c;
        }

        if (consumed > 0 && previous != '\r' && previous != '\n')
        {
            lines++; // The last line has no break after it.
        }

        var cutByBudget = consumed < read.Text.Length;
        var shownBytes = cutByBudget ? read.Encoding.GetByteCount(read.Text.AsSpan(0, consumed)) : read.ReadBytes;
        var info = string.Join(" · ", language.DisplayName,
            FormatResource(AresStrings.Peek_Lines, lines),
            read.EncodingName,
            FormatBytes(read.FileBytes));
        if (read.Truncated || cutByBudget)
        {
            info += " · " + FormatResource(AresStrings.Peek_Truncated, FormatBytes(shownBytes));
        }

        var body = new StringBuilder();
        body.Append("<div class=\"wrap\"><pre class=\"gutter\" id=\"g\" aria-hidden=\"true\"></pre>");
        body.Append("<pre class=\"code\"><code class=\"language-").Append(language.Id).Append("\">");
        body.Append(escaped).Append("</code></pre></div>");
        var script = "var g=document.getElementById('g');var n=" + lines.ToString(CultureInfo.InvariantCulture)
            + ";var a=new Array(n);for(var i=0;i<n;i++)a[i]=i+1;g.textContent=a.join('\\n');" + HighlightScript;
        return new PeekWebPage(Page(theme, CodeCss, body.ToString(), script), info);
    }

    private static PeekWebPage BuildMarkdown(TextRead read, PeekPageTheme theme)
    {
        var text = read.Text;
        var cut = false;
        string html;
        while (true)
        {
            html = Markdown.ToHtml(text, MarkdownPipeline);
            if (Encoding.UTF8.GetByteCount(html) <= ContentBudgetBytes || text.Length < 1024)
            {
                break;
            }

            // The converted page is too big for the web view: keep the first 60% of the lines and convert again.
            cut = true;
            var keep = text.LastIndexOf('\n', (int)(text.Length * 0.6));
            text = text[..(keep > 0 ? keep : (int)(text.Length * 0.6))];
        }

        var shownBytes = cut ? read.Encoding.GetByteCount(text) : read.ReadBytes;
        var info = "Markdown · " + FormatBytes(read.FileBytes);
        if (read.Truncated || cut)
        {
            info += " · " + FormatResource(AresStrings.Peek_Truncated, FormatBytes(shownBytes));
        }

        return new PeekWebPage(Page(theme, MarkdownCss, "<article class=\"md\">" + html + "</article>", HighlightScript), info);
    }

    private const string HighlightScript =
        "if(window.hljs){document.querySelectorAll('pre code[class*=\"language-\"]').forEach(function(el){"
        + "var m=/language-([\\w-]+)/.exec(el.className);if(m&&hljs.getLanguage(m[1])){hljs.highlightElement(el);}});}";

    private static string Page(PeekPageTheme theme, string css, string body, string script)
    {
        var style = theme.Dark ? "github-dark.min.css" : "github.min.css";
        var sb = new StringBuilder(body.Length + 4096);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https: data:; ")
          .Append("style-src 'unsafe-inline' https://").Append(AssetsHost)
          .Append("; script-src 'unsafe-inline' https://").Append(AssetsHost).Append("\">");
        sb.Append("<base href=\"https://").Append(FileHost).Append("/\">");
        sb.Append("<link rel=\"stylesheet\" href=\"https://").Append(AssetsHost).Append('/').Append(style).Append("\">");
        sb.Append("<style>:root{--bg:").Append(theme.Background).Append(";--fg:").Append(theme.Foreground)
          .Append(";color-scheme:").Append(theme.Dark ? "dark" : "light").Append('}').Append(css).Append("</style>");
        sb.Append("</head><body>").Append(body);
        foreach (var file in new[] { "highlight.min.js", "powershell.min.js", "dos.min.js" })
        {
            sb.Append("<script src=\"https://").Append(AssetsHost).Append('/').Append(file).Append("\"></script>");
        }

        sb.Append("<script>").Append(script).Append("</script></body></html>");
        return sb.ToString();
    }

    private const string CodeCss =
        "html,body{margin:0;background:var(--bg);color:var(--fg)}"
        + "body{font:13px/1.5 'Cascadia Mono','Cascadia Code',Consolas,monospace;tab-size:4}"
        + ".wrap{display:flex;min-width:100%;width:max-content}"
        + "pre{margin:0;font:inherit}"
        + ".gutter{position:sticky;left:0;flex:none;box-sizing:border-box;padding:12px 12px 12px 16px;text-align:right;"
        + "background:var(--bg);opacity:.55;border-right:1px solid rgba(127,127,127,.35);user-select:none}"
        + ".code{flex:1;padding:12px 24px 12px 16px;white-space:pre}"
        + "code,.hljs{font:inherit;background:transparent!important;padding:0!important;color:inherit}";

    private const string MarkdownCss =
        "html,body{margin:0;background:var(--bg);color:var(--fg)}"
        + "body{font:15px/1.6 'Segoe UI',system-ui,sans-serif}"
        + ".md{max-width:920px;margin:0 auto;padding:24px 32px 48px;overflow-wrap:anywhere}"
        + "h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.4em 0 .6em}"
        + "h1,h2{padding-bottom:.3em;border-bottom:1px solid rgba(127,127,127,.35)}"
        + "h1{font-size:2em}h2{font-size:1.5em}h3{font-size:1.25em}"
        + "a{color:#4493f8}img{max-width:100%}"
        + "pre{background:rgba(127,127,127,.14);padding:12px 16px;border-radius:6px;overflow:auto;line-height:1.45}"
        + "code{font:13px 'Cascadia Mono','Cascadia Code',Consolas,monospace}"
        + ":not(pre)>code{background:rgba(127,127,127,.2);padding:.15em .4em;border-radius:4px}"
        + "pre code,.hljs{background:transparent!important;padding:0!important}"
        + "blockquote{margin:1em 0;padding:0 1em;border-left:4px solid rgba(127,127,127,.5);opacity:.85}"
        + "table{border-collapse:collapse;display:block;overflow:auto}"
        + "th,td{border:1px solid rgba(127,127,127,.4);padding:6px 13px}th{background:rgba(127,127,127,.15)}"
        + "hr{border:0;border-top:1px solid rgba(127,127,127,.4);margin:1.6em 0}"
        + "ul.contains-task-list{list-style:none;padding-left:1.2em}";
}
