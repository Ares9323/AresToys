namespace ShareQ.App.ViewModels;

/// <summary>Builds the HTML the WebView2 navigates to for each <see cref="TraceViewMode"/>.
/// Emits a single self-contained document (CSS for checker bg + inline SVG/data-URI for the
/// source PNG) so we don't need a temp file or same-origin fetch. Outline mode rewrites the
/// trace SVG by replacing every <c>fill=</c> with <c>fill="none" stroke="#000" stroke-width="1"</c>.
///
/// Ctrl+wheel zoom: handled in JS via a CSS-transform on the artwork wrapper, NOT via
/// WebView2's native browser zoom. Native zoom scales the checker bg in lockstep with the
/// SVG, which makes the checker pattern feel like it's stealing focus while the SVG appears
/// frozen at its 95% constraint. Custom zoom keeps the checker stable (pure visual context)
/// and grows only the artwork — same convention any vector editor uses.</summary>
public static class TraceViewModeRenderer
{
    private const string CheckerCss = "html,body{margin:0;background:#222;overflow:hidden;height:100%}" +
        "body{background-image:linear-gradient(45deg,#3a3a3a 25%,transparent 25%),linear-gradient(-45deg,#3a3a3a 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#3a3a3a 75%),linear-gradient(-45deg,transparent 75%,#3a3a3a 75%);background-size:20px 20px;background-position:0 0,0 10px,10px -10px,-10px 0;background-color:#5a5a5a}" +
        // Wrapper is the transform target. Fills the viewport and uses flex so the inner
        // SVG / img is centered. transform-origin = center so Ctrl+wheel zoom feels
        // anchored. will-change hints the compositor for smoother frames.
        "#zoomwrap{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;will-change:transform;cursor:grab}" +
        "#zoomwrap:active{cursor:grabbing}" +
        // No max-width/height: JS sets explicit width/height to control fit + zoom.
        // flex-shrink:0 is critical: flex items default to flex-shrink:1, which silently
        // caps the rendered size to the flex container even when our JS sets style.width
        // to 5000px. That's why "the cap" felt hit immediately — we WERE setting huge
        // widths but the layout shrank them back to the panel size.
        "#zoomwrap svg,#zoomwrap img{display:block;flex-shrink:0}";

    // Zoom + pan implementation:
    // - Initial fit-to-window: JS computes a "fit" scale so the SVG/img occupies ~95% of
    //   the viewport at z=1 (so the user starts seeing the whole image, then zooms from there).
    // - Ctrl+wheel: multiplies user zoom factor z. Effective scale = fit * z, applied as
    //   explicit width/height on the SVG → browser re-rasterizes the SVG VECTORIALLY.
    //   Pixels stay sharp at any zoom level (within the 0.1× – 20× clamp).
    // - <img> (Source Image view): can't get vector quality out of a raster, so we use CSS
    //   transform: scale instead.
    // - Drag pans via translate on the wrapper.
    // - Double-click resets z=1 + pan=0 → snaps back to the fit-to-window.
    // The script wires zoom/pan AND exposes window.shareqSetContent(html) so the host can
    // swap the SVG/img inside #zoomwrap without ever re-navigating the document. Keeping
    // the document constant means z/tx/ty (the user's zoom + pan state) survive every
    // re-trace caused by a parameter change — the user can zoom into a problem area, tweak
    // a parameter, and stay zoomed where they were. Recomputing fit on each swap covers
    // dimension changes (e.g. switching to Source Image view).
    private const string ZoomScript =
        "<script>(()=>{let z=1,tx=0,ty=0,fit=1;let targets=[],bases=[];" +
        "const w=document.getElementById('zoomwrap');" +
        "if(!w)return;" +
        "function applyZoom(){if(!targets.length)return;const eff=fit*z;targets.forEach((t,i)=>{const b=bases[i];" +
        "if(b.svg){t.style.width=(b.w*eff)+'px';t.style.height=(b.h*eff)+'px'}" +
        "else{t.style.transformOrigin='center center';t.style.transform=`scale(${eff})`}" +
        "})}" +
        "function applyPan(){w.style.transform=`translate(${tx}px,${ty}px)`}" +
        // Re-acquire targets+bases after content swaps. fit is recomputed (image dims may
        // have changed when switching view modes); z stays as the user left it so the
        // perceived zoom level is preserved across re-traces.
        "function recompute(){targets=[...w.querySelectorAll('svg,img')];" +
        "if(!targets.length){applyPan();return}" +
        "const vw=window.innerWidth,vh=window.innerHeight;" +
        "bases=targets.map(t=>{" +
        "if(t.tagName.toLowerCase()==='svg'&&t.viewBox&&t.viewBox.baseVal.width>0)return{w:t.viewBox.baseVal.width,h:t.viewBox.baseVal.height,svg:true};" +
        "return{w:t.naturalWidth||t.getBoundingClientRect().width||300,h:t.naturalHeight||t.getBoundingClientRect().height||150,svg:false}" +
        "});" +
        "fit=Math.min(...bases.map(b=>Math.min(vw/b.w,vh/b.h)*0.95));" +
        "applyZoom();applyPan()}" +
        // Wait one rAF tick so getBoundingClientRect / naturalWidth are populated.
        "requestAnimationFrame(recompute);" +
        // Host calls this after each re-trace. innerHTML swap is cheap, leaves the wrapper +
        // its event listeners intact (handlers are bound to w, not the children). For images
        // that haven't loaded yet (data: URI decode latency), schedule a second recompute
        // on load so naturalWidth is available before we set the fit factor.
        "window.shareqSetContent=function(html){w.innerHTML=html;requestAnimationFrame(recompute);" +
        "w.querySelectorAll('img').forEach(img=>{if(!img.complete)img.addEventListener('load',recompute,{once:true})})};" +
        "window.addEventListener('wheel',e=>{if(!e.ctrlKey)return;e.preventDefault();" +
        // Early-return at the cap so we don't accumulate phantom zoom: if the user keeps
        // wheeling up past the max, internally z just stays at 100 (no growth) and the next
        // wheel-down immediately starts decreasing. Without this, even though Math.min
        // clamps the value, some platforms perceive the rapid clamped-to-same-value calls
        // as "accumulating zoom intent" — the explicit guard makes the boundary crisp.
        "const dy=e.deltaY;if(dy<0&&z>=100)return;if(dy>0&&z<=0.05)return;" +
        "const f=dy<0?1.1:1/1.1;z=Math.max(0.05,Math.min(100,z*f));applyZoom()" +
        "},{passive:false});" +
        "let drag=null;w.addEventListener('pointerdown',e=>{" +
        "if(e.button!==0)return;drag={x:e.clientX-tx,y:e.clientY-ty};w.setPointerCapture(e.pointerId)});" +
        "w.addEventListener('pointermove',e=>{if(!drag)return;tx=e.clientX-drag.x;ty=e.clientY-drag.y;applyPan()});" +
        "w.addEventListener('pointerup',e=>{drag=null;try{w.releasePointerCapture(e.pointerId)}catch{}});" +
        "window.addEventListener('dblclick',()=>{z=1;tx=0;ty=0;applyZoom();applyPan()});" +
        "})();</script>";

    /// <summary>Build the full HTML document — used for the FIRST navigation only. Subsequent
    /// re-traces use <see cref="RenderBody"/> + the JS swap path so the document (and the
    /// user's zoom / pan state inside it) survives the update.</summary>
    public static string Render(string? svg, byte[]? sourcePng, TraceViewMode mode)
        => Wrap(RenderBody(svg, sourcePng, mode));

    /// <summary>Build only the inner HTML that goes inside <c>#zoomwrap</c>. The host calls
    /// <c>window.shareqSetContent(this)</c> via ExecuteScriptAsync to swap in the new render
    /// without touching the document — preserves z/tx/ty across parameter tweaks so the
    /// user can stay zoomed into a problem area while iterating on settings.</summary>
    public static string RenderBody(string? svg, byte[]? sourcePng, TraceViewMode mode)
    {
        if (mode == TraceViewMode.SourceImage)
        {
            return $"<img src=\"{DataUri(sourcePng)}\" />";
        }
        if (string.IsNullOrEmpty(svg))
        {
            return "<div style='color:#aaa;font-family:sans-serif'>(no output)</div>";
        }
        return mode switch
        {
            TraceViewMode.TracingResult => svg,
            TraceViewMode.TracingResultWithOutlines => LayerOver(svg, MakeOutlines(svg)),
            TraceViewMode.Outlines => MakeOutlines(svg),
            TraceViewMode.OutlinesWithSource => $"<img src=\"{DataUri(sourcePng)}\" style='position:absolute' />" +
                                                $"<div style='position:relative'>{MakeOutlines(svg)}</div>",
            _ => svg,
        };
    }

    /// <summary>Wrap the inner body in a zoomable container. <c>#zoomwrap</c> is the element
    /// the JS scales / pans. The body has overflow:hidden so the artwork can extend beyond
    /// the viewport during zoom without showing scrollbars (drag-to-pan brings off-screen
    /// regions back into view).</summary>
    private static string Wrap(string body) =>
        $"<!doctype html><html><head><style>{CheckerCss}</style></head><body>" +
        $"<div id=\"zoomwrap\">{body}</div>{ZoomScript}</body></html>";

    /// <summary>Layer two SVG fragments: produce a single positioned container with the
    /// filled SVG behind and the outline SVG in front. Trace SVG is positionless inline,
    /// so absolute-positioning a wrapper is enough.</summary>
    private static string LayerOver(string under, string over) =>
        $"<div style='position:relative;display:flex;align-items:center;justify-content:center'>" +
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
