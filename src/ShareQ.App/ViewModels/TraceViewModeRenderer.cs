namespace ShareQ.App.ViewModels;

/// <summary>Builds the HTML the WebView2 navigates to for each <see cref="TraceViewMode"/>.
/// Emits a single self-contained document (CSS for checker bg + inline SVG/data-URI for the
/// source PNG) so we don't need a temp file or same-origin fetch. Outline mode rewrites the
/// trace SVG by replacing every <c>fill=</c> with <c>fill="none" stroke="#000" stroke-width="1"</c>.</summary>
public static class TraceViewModeRenderer
{
    private const string CheckerCss = "html,body{margin:0;background:#222}body{display:flex;align-items:center;justify-content:center;height:100vh;background-image:linear-gradient(45deg,#3a3a3a 25%,transparent 25%),linear-gradient(-45deg,#3a3a3a 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#3a3a3a 75%),linear-gradient(-45deg,transparent 75%,#3a3a3a 75%);background-size:20px 20px;background-position:0 0,0 10px,10px -10px,-10px 0;background-color:#5a5a5a}svg,img{max-width:95%;max-height:95%}";

    public static string Render(string? svg, byte[]? sourcePng, TraceViewMode mode)
    {
        if (mode == TraceViewMode.SourceImage)
        {
            return Wrap($"<img src=\"{DataUri(sourcePng)}\" />");
        }
        if (string.IsNullOrEmpty(svg))
        {
            return Wrap("<div style='color:#aaa;font-family:sans-serif'>(no output)</div>");
        }
        return mode switch
        {
            TraceViewMode.TracingResult => Wrap(svg),
            TraceViewMode.TracingResultWithOutlines => Wrap(LayerOver(svg, MakeOutlines(svg))),
            TraceViewMode.Outlines => Wrap(MakeOutlines(svg)),
            TraceViewMode.OutlinesWithSource => Wrap($"<img src=\"{DataUri(sourcePng)}\" style='position:absolute' />" +
                                                     $"<div style='position:relative'>{MakeOutlines(svg)}</div>"),
            _ => Wrap(svg),
        };
    }

    private static string Wrap(string body) =>
        $"<!doctype html><html><head><style>{CheckerCss}</style></head><body>{body}</body></html>";

    /// <summary>Layer two SVG fragments: produce a single positioned container with the
    /// filled SVG behind and the outline SVG in front. Trace SVG is positionless inline,
    /// so absolute-positioning a wrapper is enough.</summary>
    private static string LayerOver(string under, string over) =>
        $"<div style='position:relative;width:100%;height:100%;display:flex;align-items:center;justify-content:center'>" +
        $"<div style='position:absolute'>{under}</div><div style='position:absolute'>{over}</div></div>";

    /// <summary>Cheap "outlines" pass: regex-replace every <c>fill="…"</c> with
    /// <c>fill="none" stroke="#000" stroke-width="1"</c>. Strokes only show on path
    /// boundaries — exactly what Illustrator's Outlines view does. Not perfect (won't
    /// touch <c>style="fill:…"</c> if some upstream tool produces that variant) but
    /// our potrace output uses the attribute form so it's reliable in practice.</summary>
    private static string MakeOutlines(string svg) =>
        System.Text.RegularExpressions.Regex.Replace(
            svg,
            "fill=\"[^\"]*\"",
            "fill=\"none\" stroke=\"#000\" stroke-width=\"1\"");

    private static string DataUri(byte[]? png) =>
        png is { Length: > 0 } ? $"data:image/png;base64,{Convert.ToBase64String(png)}" : "";
}
