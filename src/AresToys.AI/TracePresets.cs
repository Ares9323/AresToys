namespace AresToys.AI;

/// <summary>Built-in trace presets. The numeric values follow VectorCraft's presets of the
/// same name (colours, paths, corners, noise, method, snap). "1 Color" is the black and
/// white trace with the ink colour kept and white never traced. The "(transparent)"
/// variants ignore white, so the background becomes transparent; change the ignored colour
/// with the eyedropper or the swatch next to Ignore Color.</summary>
public sealed record TracePreset(string Name, TraceOptions Options);

public static class TracePresets
{
    /// <summary>Pure white = (255, 255, 255). Ignored by the transparent variants.</summary>
    private static readonly System.Drawing.Color White = System.Drawing.Color.FromArgb(255, 255, 255, 255);

    private static TracePreset Bw(string name, int threshold, int paths, int corners, int noise, bool snap) =>
        new(name, new TraceOptions(
            Mode: TraceMode.BlackAndWhite,
            Threshold: threshold,
            PathsPercent: paths,
            CornersPercent: corners,
            NoisePx: noise,
            Method: TraceMethod.Abutting,
            SnapCurvesToLines: snap));

    private static TracePreset Color(string name, TracePalette palette, int colors, int paths, int corners,
        int noise, TraceMethod method, bool transparent = false) =>
        new(name, new TraceOptions(
            Mode: TraceMode.Color,
            Palette: palette,
            ColorCount: colors,
            PathsPercent: paths,
            CornersPercent: corners,
            NoisePx: noise,
            Method: method,
            SnapCurvesToLines: false,
            IgnoreColor: transparent ? White : null,
            IgnoreTolerance: 32));

    private static TracePreset Limited(int n, int paths, int corners, int noise, bool transparent = false) =>
        Color(transparent ? $"{n} Colors (transparent)" : $"{n} Colors", TracePalette.Limited, n,
            paths, corners, noise, TraceMethod.Abutting, transparent);

    public static IReadOnlyList<TracePreset> Stock { get; } = new[]
    {
        Bw("[Default]", 128, 50, 75, 25, snap: false),
        new TracePreset("Auto Color", new TraceOptions(
            Mode: TraceMode.Color,
            Palette: TracePalette.Automatic,
            ColorCount: 6,
            ColorDetail: 85,
            PathsPercent: 65,
            CornersPercent: 60,
            NoisePx: 16,
            Method: TraceMethod.Abutting,
            SnapCurvesToLines: false)),
        Color("High Fidelity Photo", TracePalette.Limited, 64, 90, 25, 4, TraceMethod.Overlapping),
        Color("Low Fidelity Photo", TracePalette.Limited, 20, 60, 50, 12, TraceMethod.Overlapping),
        Bw("1 Color", 128, 80, 70, 10, snap: false),
        Limited(2, 60, 60, 20),
        Limited(3, 60, 60, 20),
        Limited(4, 65, 60, 18),
        Limited(5, 65, 60, 16),
        Limited(6, 65, 60, 16),
        Limited(2, 60, 60, 20, transparent: true),
        Limited(3, 60, 60, 20, transparent: true),
        Limited(4, 65, 60, 18, transparent: true),
        Limited(5, 65, 60, 16, transparent: true),
        Limited(6, 65, 60, 16, transparent: true),
        Limited(16, 70, 55, 10),
        new TracePreset("Shades of Gray", new TraceOptions(
            Mode: TraceMode.Grayscale,
            Palette: TracePalette.Limited,
            ColorCount: 8,
            PathsPercent: 60,
            CornersPercent: 50,
            NoisePx: 10,
            Method: TraceMethod.Overlapping,
            SnapCurvesToLines: false)),
        Bw("Black and White Logo", 128, 95, 80, 8, snap: true),
        Bw("Sketched Art", 150, 50, 50, 100, snap: false),
        Bw("Silhouettes", 200, 40, 60, 30, snap: false),
        Bw("Line Art", 128, 80, 70, 10, snap: false),
        Bw("Technical Drawing", 128, 95, 90, 4, snap: true),
        new TracePreset("Outline", new TraceOptions(
            Mode: TraceMode.BlackAndWhite,
            Threshold: 128,
            PathsPercent: 80,
            CornersPercent: 70,
            NoisePx: 10,
            Method: TraceMethod.Abutting,
            SnapCurvesToLines: false,
            Fills: false,
            Strokes: true)),
        new TracePreset("Flat Logo", new TraceOptions(
            Mode: TraceMode.Logo,
            ColorCount: 8,
            PathsPercent: 60,
            CornersPercent: 50,
            NoisePx: 6,
            Method: TraceMethod.Abutting,
            SnapCurvesToLines: true)),
    };
}
