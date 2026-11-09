using System.Globalization;
using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;
using AresToys.Editor.Model;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Converts the colour produced by an upstream <c>ColorSampler</c> / <c>ColorPicker</c> step
/// into a textual representation (hex / rgb / hsb / cmyk / decimal / UE FLinearColor / UE FColor)
/// and writes it into the pipeline bag as <c>bag.text</c> + <c>bag.payload_bytes</c>. Composes
/// with downstream <c>arestoys.add-text</c> (copy to Windows clipboard, add to AresToys history)
/// the same way image-capture workflows chain Save → AddToHistory: this step is the pure
/// converter, the next step is the sink.
/// <para>
/// Supersedes the 8 separate <c>CopyColorAs*</c> tasks (kept around as runtime classes for
/// backward compat with profiles authored before this consolidation). Format is selected via
/// <c>config.format</c> from a dropdown — defaults to <c>"hex"</c>.
/// </para>
/// </summary>
public sealed class ConvertColorTask : IPipelineTask
{
    public const string TaskId = "arestoys.convert-color";

    public string Id => TaskId;
    public string DisplayName => "Convert color";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        if (!context.Bag.TryGetValue(PipelineBagKeys.Color, out var raw) || raw is not ShapeColor c)
        {
            // No colour from upstream — same abort behaviour as the legacy CopyColorAs* tasks so
            // a wiring mistake (Convert color without a sampler/picker before it) surfaces with a
            // useful error instead of silently writing nothing.
            context.Abort($"{Id}: no color in bag — add a Color sampler or Color picker step first");
            return Task.CompletedTask;
        }

        var format = ((string?)config?["format"])?.Trim().ToLowerInvariant() ?? "hex";
        var text = Format(c, format);

        // Only bag.text is set — no PayloadBytes / FileExtension. The previous draft also wrote
        // UTF-8 bytes "for chaining" but no downstream task actually wants the colour text as a
        // .txt payload; declaring the output as Text-only also makes the workflow editor's port
        // visualization honest (Text out, no Payload).
        context.Bag[PipelineBagKeys.Text] = text;
        return Task.CompletedTask;
    }

    /// <summary>Pure converter — exposed so unit tests (and any future callers that want a colour
    /// string without going through the pipeline) can reuse the same format table.</summary>
    public static string Format(ShapeColor c, string format) => format switch
    {
        "hex"                => $"{c.R:X2}{c.G:X2}{c.B:X2}",
        "hex-hash"           => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
        "hex-alpha"          => $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}",
        "hex-hash-alpha"     => $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}",
        "rgb"                => $"rgb({c.R}, {c.G}, {c.B})",
        "rgba"               => $"rgba({c.R}, {c.G}, {c.B}, {(c.A / 255.0).ToString("0.##", CultureInfo.InvariantCulture)})",
        "hsb"                => FormatHsb(c),
        "cmyk"               => FormatCmyk(c),
        "decimal"            => (((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B)
                                    .ToString(CultureInfo.InvariantCulture),
        "linear"             => FormatLinear(c),
        "bgra"               => $"(B={c.B},G={c.G},R={c.R},A={c.A})",
        // Unknown / typo → fall back to plain hex. Same shape the user gets from the dropdown's
        // default, so misconfigured profiles still produce a sensible string.
        _                    => $"{c.R:X2}{c.G:X2}{c.B:X2}",
    };

    private static string FormatHsb(ShapeColor c)
    {
        var hsv = Hsv.FromRgb(c.R, c.G, c.B);
        return $"hsb({(int)Math.Round(hsv.H * 360)}°, {(int)Math.Round(hsv.S * 100)}%, {(int)Math.Round(hsv.V * 100)}%)";
    }

    private static string FormatCmyk(ShapeColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var k = 1.0 - Math.Max(r, Math.Max(g, b));
        double cy, m, y;
        if (k >= 0.99999) { cy = m = y = 0; k = 1.0; }
        else
        {
            cy = (1 - r - k) / (1 - k);
            m  = (1 - g - k) / (1 - k);
            y  = (1 - b - k) / (1 - k);
        }
        string F(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture);
        return $"cmyk({F(cy)}%, {F(m)}%, {F(y)}%, {F(k)}%)";
    }

    private static string FormatLinear(ShapeColor c)
    {
        string F(byte ch) => (ch / 255.0).ToString("0.000000", CultureInfo.InvariantCulture);
        return $"(R={F(c.R)},G={F(c.G)},B={F(c.B)},A={F(c.A)})";
    }
}
