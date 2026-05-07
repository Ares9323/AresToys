namespace ShareQ.AI;

public enum TraceMode { BlackAndWhite, Grayscale, Color }

/// <summary>Palette generation strategy for Color/Grayscale modes.
/// <c>Limited</c> = use <c>ColorCount</c> as the requested count (default).
/// <c>Automatic</c> = let the quantizer pick a count by elbow-point heuristic
/// (kept simple: cap at <c>ColorCount</c> but drop colours whose share &lt; 2%).
/// <c>FullTone</c> = no quantization, treat every distinct source colour as a layer
/// (impractical for screenshots; included for parity with Illustrator).</summary>
public enum TracePalette { Limited, Automatic, FullTone }

/// <summary>How adjacent colour layers stitch in multi-colour mode.
/// <c>Overlapping</c> = paths overlap on colour boundaries (no gaps; the upper layer
/// covers the seam). <c>Abutting</c> = paths share edges with no overlap (each pixel
/// belongs to exactly one layer; cleaner editing in Illustrator).</summary>
public enum TraceMethod { Overlapping, Abutting }

/// <summary>The complete parameter snapshot a trace run consumes. Mirrors
/// Illustrator's Image Trace panel: see the README's "Reference UX" section for the
/// per-field mapping back to potrace CLI flags.
///
/// Field-by-field semantics:
/// • <see cref="Mode"/>: BW = mono silhouette via <see cref="Threshold"/>; Gray =
///   quantize to N gray levels; Color = quantize to N colours.
/// • <see cref="Palette"/>: Limited (use ColorCount), Automatic (elbow-prune), FullTone
///   (no quantization — every unique source colour). Ignored when Mode = BW.
/// • <see cref="ColorCount"/>: 2-30 — applies to Color/Gray modes.
/// • <see cref="Threshold"/>: 0-255 luminance cutoff for BW mode (Illustrator default 128).
/// • <see cref="PathsPercent"/>: 0-100 — Illustrator "Paths" slider. Low = few paths
///   (aggressive curve merging, lossy); High = preserve every path (high fidelity).
///   Maps to potrace <c>-O</c> as <c>1.0 - PathsPercent/100</c> (inverted; Illustrator
///   default 50% → -O 0.5, default for typical icons).
/// • <see cref="CornersPercent"/>: 0-100 — Illustrator "Corners" slider. Less = smoother
///   curves (more rounding); More = preserve sharp corners. Maps to potrace <c>-a</c>
///   alphamax as <c>1.3 * (1.0 - CornersPercent/100)</c>: 0% → 1.3 (max smooth), 100% →
///   0 (polygon). Illustrator default 75% → ≈ 0.325.
/// • <see cref="NoisePx"/>: 1-100 — potrace <c>-t</c> turdsize. Drops connected regions
///   smaller than N pixels. Illustrator default 25.
/// • <see cref="Method"/>: only consumed in Color/Gray mode (see <see cref="TraceMethod"/>).
/// • <see cref="SnapCurvesToLines"/>: when on, post-process near-straight bezier segments
///   to actual line segments. Maps to potrace default behaviour; <c>off</c> sets <c>-n</c>
///   (no curve-to-line conversion).
/// • <see cref="Transparency"/>: Color/Gray mode only — when on, the IgnoreColor layer
///   becomes transparent in the output SVG instead of being filled. When off and
///   IgnoreColor is set, the matching pixels render as <c>IgnoreColor</c> in the
///   composite (so the SVG keeps an opaque background).
/// • <see cref="IgnoreColor"/> + <see cref="IgnoreTolerance"/>: pixels within tolerance
///   of this colour are dropped from the trace (eyedropper-driven). Maps to our existing
///   per-pixel mask in <c>EncodePbm</c>.
/// • <see cref="AutoGrouping"/>: presentational — wraps each colour layer in a labelled
///   <c>&lt;g id="…"/&gt;</c> in the composite SVG so Illustrator/Inkscape can pick them
///   apart. No effect on tracing math.
/// • <see cref="SmoothingIterations"/>: 0-3 — number of 3x3 majority-filter passes over
///   the per-pixel layer assignment before tracing. 0 = off (raw quantisation, traces
///   every anti-alias zigzag). 1 = collapse single-pixel oscillations. 2 = also catch
///   2-pixel notches/bumps (default). 3+ starts eating ≤1px features.
/// • <see cref="PreBlurStrength"/>: 0-3 — number of 3x3 box-blur passes applied to the
///   source bitmap before quantisation. 0 = none (raw source, anti-aliased pixels stay
///   ambiguous). 1 = single pass (default; cleans most screenshot AA). 2-3 = stronger,
///   softens fine detail but produces cleaner colour boundaries on noisy sources.
/// • <see cref="OverlapRadius"/>: 0-3 — pixels each layer's mask is dilated into adjacent
///   layers in <see cref="TraceMethod.Overlapping"/> mode, to close the smoothing-driven
///   gaps between potrace's per-layer paths. 0 = strict partition (gaps may show).
///   1 = default (closes most gaps). 2-3 = aggressive overlap, may show tinted halo on
///   the smaller layer's silhouette but eliminates the worst gap cases. Has no effect
///   in <see cref="TraceMethod.Abutting"/> mode.</summary>
public sealed record TraceOptions(
    TraceMode Mode = TraceMode.BlackAndWhite,
    TracePalette Palette = TracePalette.Limited,
    int ColorCount = 6,
    int Threshold = 128,
    int PathsPercent = 50,
    int CornersPercent = 75,
    int NoisePx = 25,
    TraceMethod Method = TraceMethod.Overlapping,
    bool SnapCurvesToLines = true,
    bool Transparency = false,
    System.Drawing.Color? IgnoreColor = null,
    int IgnoreTolerance = 32,
    bool AutoGrouping = true,
    int SmoothingIterations = 2,
    int PreBlurStrength = 1,
    int OverlapRadius = 1);
