using System.Text.RegularExpressions;

namespace AresToys.App.Services;

/// <summary>Fallback plaintext extractors used by AutoPaster when an item's stored SearchText is
/// empty (rare — usually the clipboard reader captures CF_UNICODETEXT alongside HTML/RTF).</summary>
internal static class ClipboardCleaning
{
    public static string HtmlToPlain(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Strip the CF_HTML header block ("Version:1.0\r\nStartHTML:N\r\n...") that prefixes
        // every clipboard HTML payload. Most producers also emit <!--StartFragment-->/
        // <!--EndFragment--> comments inside the body so the marker-based slice below kicks
        // in and the header gets discarded as a side effect — but some apps (Rider64.exe is
        // the one that surfaced the bug) write a minimal CF_HTML with the header followed by
        // raw text and NO fragment comments. Without this preamble pass the headers would
        // paste through verbatim ("Version:1.0\nStartHTML:0000000128\n...Whitelist").
        if (html.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = 0;
            while (idx < html.Length)
            {
                var lineEnd = html.IndexOf('\n', idx);
                if (lineEnd < 0) break;
                // A header line is "Word:..." appearing before any '<' on that line. As soon
                // as we hit a line that doesn't fit that shape (blank line, opening tag, or
                // body text), the header block is over and the rest is the actual content.
                var line = html.AsSpan(idx, lineEnd - idx);
                var colon = line.IndexOf(':');
                var lt = line.IndexOf('<');
                if (colon > 0 && (lt < 0 || lt > colon))
                {
                    idx = lineEnd + 1;
                    continue;
                }
                break;
            }
            html = html[idx..];
        }

        var fragStart = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragStart >= 0) html = html[(fragStart + "<!--StartFragment-->".Length)..];
        var fragEnd = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (fragEnd >= 0) html = html[..fragEnd];

        // Drop entire <style>…</style> + <script>…</script> + <head>…</head> blocks WITH their
        // body — the generic tag-strip below would only remove the open/close tags and leak the
        // CSS/JS source code into the output. Qt / KDE apps + Outlook + some browsers ship a
        // <style>p, li { white-space: pre-wrap; }</style> preamble on every HTML clipboard
        // payload that without this would paste through verbatim above the actual text. Same
        // pattern handles inline <script> bodies and any stray <head> metadata.
        // [\s\S]*? matches across newlines (HTML content is multi-line by nature) without
        // requiring RegexOptions.Singleline; the lazy quantifier prevents matches from spanning
        // adjacent <style> blocks on the same page.
        html = Regex.Replace(html, @"<style\b[^>]*>[\s\S]*?</style\s*>", string.Empty, RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<script\b[^>]*>[\s\S]*?</script\s*>", string.Empty, RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<head\b[^>]*>[\s\S]*?</head\s*>", string.Empty, RegexOptions.IgnoreCase);
        // HTML comments — anywhere in the residual body. StartFragment / EndFragment markers
        // were already consumed above; this catches everything else (author notes, conditional
        // IE comments, etc.) that the tag-strip pass would otherwise leave behind as text.
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", string.Empty);

        // Process <pre>…</pre> blocks specially BEFORE the global block-tag → newline pass.
        // Qt rich text + Outlook + some IDEs emit <br> inside <pre> as "soft wrap" (visual
        // wrap-around at the editor's column width — not a logical line break). The global
        // <br> → \n rule below would shred ASCII art that originally fit on one line. Inside
        // a <pre> block we KEEP whatever real \n was already there, but DROP any <br> (treat
        // them as soft wrap), and strip inner inline tags. The cleaned inner content replaces
        // the whole block; the outer </pre> wouldn't add a newline anymore (the match consumes
        // it) so we tack one on explicitly to separate from following content.
        html = Regex.Replace(html, @"<pre\b[^>]*>([\s\S]*?)</pre\s*>", m =>
        {
            var inner = m.Groups[1].Value;
            inner = Regex.Replace(inner, @"<br\s*/?>", string.Empty, RegexOptions.IgnoreCase);
            inner = Regex.Replace(inner, "<[^>]+>", string.Empty);
            return inner + "\n";
        }, RegexOptions.IgnoreCase);

        // Block-level tags get converted to newlines BEFORE the generic tag-strip so the
        // resulting plaintext keeps line structure. Without this, copying an ASCII-art block
        // from a web page (or any paragraph-heavy snippet) collapses to a single line because
        // the previous strip-then-whitespace-collapse step erased every \n.
        // Order matters: <br> first, then closing block tags, then opening block tags. We
        // insert one '\n' for closing tags and leave the rest in place — the strip pass below
        // removes the tag itself, leaving the newline as a real plaintext break. The <pre>
        // entry stays in the closing-block list as a defensive no-op — by this point every
        // <pre>…</pre> pair has already been consumed by the dedicated pass above.
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</(p|div|tr|li|h[1-6]|pre|blockquote|article|section|header|footer|nav|aside|table|thead|tbody|tfoot|ul|ol|dl|dt|dd|figure|figcaption)\s*>", "\n", RegexOptions.IgnoreCase);

        // Now strip every remaining tag with empty replacement (NOT space) so inline tags
        // like <span> / <strong> don't insert spurious gaps between adjacent characters in
        // pre-formatted blocks.
        html = Regex.Replace(html, "<[^>]+>", string.Empty);
        html = System.Net.WebUtility.HtmlDecode(html);
        // Normalise CRLF / lone CR to LF so downstream consumers see a single line ending.
        // Don't collapse \s+ — that would destroy ASCII-art alignment (consecutive spaces are
        // meaningful inside <pre> blocks). Only fold runs of 3+ newlines so a paragraph-soup
        // page doesn't paste with comically tall vertical gaps; up to 2 consecutive newlines
        // (the standard "blank line between paragraphs" rhythm) survive unchanged.
        html = html.Replace("\r\n", "\n").Replace('\r', '\n');
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    public static string RtfToPlain(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return string.Empty;
        rtf = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", " ");
        rtf = Regex.Replace(rtf, @"\\[a-zA-Z]+-?\d* ?", " ");
        rtf = rtf.Replace("{", " ").Replace("}", " ");
        rtf = Regex.Replace(rtf, @"\s+", " ").Trim();
        return rtf;
    }
}
