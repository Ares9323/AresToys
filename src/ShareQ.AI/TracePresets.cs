namespace ShareQ.AI;

/// <summary>Built-in trace presets matching Illustrator's Image Trace dropdown verbatim
/// where the parameters map cleanly to potrace, and approximated where Illustrator's
/// proprietary tracer has no direct potrace equivalent (notably "Sketched Art" /
/// "Technical Drawing" — those rely on Illustrator's centerline tracer; we approximate
/// with high-detail outlines + low despeckle, which gets close but isn't identical).</summary>
public sealed record TracePreset(string Name, TraceOptions Options);

public static class TracePresets
{
    public static IReadOnlyList<TracePreset> Stock { get; } = new[]
    {
        new TracePreset("[Default]", new TraceOptions()),
        new TracePreset("High Fidelity Photo", new(
            Mode: TraceMode.Color, Palette: TracePalette.FullTone, ColorCount: 30,
            PathsPercent: 80, CornersPercent: 75, NoisePx: 5)),
        new TracePreset("Low Fidelity Photo", new(
            Mode: TraceMode.Color, Palette: TracePalette.Limited, ColorCount: 16,
            PathsPercent: 30, CornersPercent: 75, NoisePx: 50)),
        new TracePreset("3 Colors", new(Mode: TraceMode.Color, ColorCount: 3)),
        new TracePreset("6 Colors", new(Mode: TraceMode.Color, ColorCount: 6)),
        new TracePreset("16 Colors", new(Mode: TraceMode.Color, ColorCount: 16)),
        new TracePreset("Shades of Gray", new(
            Mode: TraceMode.Grayscale, ColorCount: 16, NoisePx: 25)),
        new TracePreset("Black and White Logo", new(
            Mode: TraceMode.BlackAndWhite, Threshold: 128, NoisePx: 25)),
        new TracePreset("Sketched Art", new(
            Mode: TraceMode.BlackAndWhite, Threshold: 100, NoisePx: 4,
            PathsPercent: 80, CornersPercent: 50)),
        new TracePreset("Silhouettes", new(
            Mode: TraceMode.BlackAndWhite, Threshold: 128, NoisePx: 100,
            PathsPercent: 50, CornersPercent: 0)),
        new TracePreset("Line Art", new(
            Mode: TraceMode.BlackAndWhite, Threshold: 128, NoisePx: 25,
            PathsPercent: 95, CornersPercent: 75, SnapCurvesToLines: true)),
        new TracePreset("Technical Drawing", new(
            Mode: TraceMode.BlackAndWhite, Threshold: 128, NoisePx: 1,
            PathsPercent: 100, CornersPercent: 100, SnapCurvesToLines: true)),
    };
}
