namespace AresToys.AI;

/// <summary><c>Logo</c> = VectorCraft's Flat Logo mode for flat colour artwork. Colours are
/// found automatically, or taken from <see cref="TraceOptions.CustomPalette"/> (up to 10).</summary>
public enum TraceMode { BlackAndWhite, Grayscale, Color, Logo }

/// <summary>Palette generation strategy for Color/Grayscale modes.
/// <c>Limited</c> = use <c>ColorCount</c> as the requested count (default).
/// <c>Automatic</c> = let the quantizer pick a count by elbow-point heuristic
/// (kept simple: cap at <c>ColorCount</c> but drop colours whose share &lt; 2%).
/// <c>FullTone</c> = no quantization, treat every distinct source colour as a layer
/// (impractical for screenshots; included for parity with Illustrator).
/// <c>Custom</c> = use the user-picked palette in <see cref="TraceOptions.CustomPalette"/>;
/// every source pixel maps to its nearest entry by Euclidean RGB distance. Lets the user
/// fold related tones (e.g. white + light grey collapsed into "white" by sampling only
/// white). Falls back to Limited when the custom list is empty.</summary>
public enum TracePalette { Limited, Automatic, FullTone, Custom }

/// <summary>How adjacent colour layers stitch in multi-colour mode.
/// <c>Overlapping</c> = paths overlap on colour boundaries (no gaps; the upper layer
/// covers the seam). <c>Abutting</c> = paths share edges with no overlap (each pixel
/// belongs to exactly one layer; cleaner editing in Illustrator).</summary>
public enum TraceMethod { Overlapping, Abutting }

/// <summary>The complete parameter snapshot a trace run consumes. Mirrors
/// Illustrator's Image Trace panel; consumed by <see cref="VectorCraftImageTracer"/>.
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
///   Illustrator default 50%.
/// • <see cref="CornersPercent"/>: 0-100 — Illustrator "Corners" slider. Less = smoother
///   curves (more rounding); More = preserve sharp corners. ///   Illustrator default 75%.
/// • <see cref="NoisePx"/>: 1-100 — Despeckle: drops connected regions
///   smaller than N pixels. Illustrator default 25.
/// • <see cref="Method"/>: only consumed in Color/Gray mode (see <see cref="TraceMethod"/>).
/// • <see cref="SnapCurvesToLines"/>: when on, post-process near-straight bezier segments
///   to actual line segments.
/// • <see cref="IgnoreColor"/> + <see cref="IgnoreTolerance"/>: pixels within tolerance
///   of this colour are dropped from the trace (eyedropper-driven). Matching pixels
///   become transparent before tracing, in any mode.
/// • <see cref="AutoGrouping"/>: presentational — wraps each colour layer in a labelled
///   <c>&lt;g id="…"/&gt;</c> in the composite SVG so Illustrator/Inkscape can pick them
///   apart. No effect on tracing math.
/// • <see cref="PreBlurStrength"/>: 0-3 — number of 3x3 box-blur passes applied to the
///   source bitmap before quantisation. 0 = none (raw source, anti-aliased pixels stay
///   ambiguous). 1 = single pass (default; cleans most screenshot AA). 2-3 = stronger,
///   softens fine detail but produces cleaner colour boundaries on noisy sources.
/// • <see cref="ColorDetail"/>: 0-100 — how closely the colour quantiser follows
///   small colour differences. Low = merge similar tones, high = keep them apart
///   (default 50).
/// • <see cref="Shapes"/>: when on, recognised primitives (circles, rectangles) are
///   written as shape elements instead of paths (default off).
/// • <see cref="Gradients"/>: when on, smooth colour ramps are detected and written as
///   SVG gradients (default off).
/// • <see cref="Fills"/>: when on, regions are written as filled paths (default on).
/// • <see cref="Strokes"/>: when on, thin features are written as stroked centre lines
///   (default off).
/// • <see cref="StrokeWidth"/>: maximum width in pixels of a feature that becomes a
///   stroke when <see cref="Strokes"/> is on (default 10).</summary>
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
    System.Drawing.Color? IgnoreColor = null,
    int IgnoreTolerance = 32,
    bool AutoGrouping = true,
    int PreBlurStrength = 1,
    /// <summary>User-picked palette consumed when <see cref="Palette"/> = Custom. Each
    /// source pixel is mapped to its nearest entry in this list (Euclidean RGB). Empty
    /// list = falls back to Limited so the user always gets a trace; the typical workflow
    /// is "pick 3 colours, get a 3-layer trace where related tones collapse into the
    /// nearest pick".</summary>
    System.Collections.Generic.IReadOnlyList<System.Drawing.Color>? CustomPalette = null,
    int ColorDetail = 50,
    bool Shapes = false,
    bool Gradients = false,
    bool Fills = true,
    bool Strokes = false,
    double StrokeWidth = 10);
