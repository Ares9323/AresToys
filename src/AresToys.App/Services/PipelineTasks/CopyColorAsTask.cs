using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using AresToys.Core.Pipeline;
using AresToys.Editor.Model;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Reads <see cref="PipelineBagKeys.Color"/> set by an upstream sampler / picker step
/// and copies the colour to the clipboard in a specific format. One concrete subclass per format
/// (Hex / RGB / RGBA / HSB / CMYK / Decimal / Linear / BGRA) — keeps the workflow editor's
/// "+ Add step" picker simple (one entry per format, no dropdown config) and avoids JSON-edit
/// friction.</summary>
public abstract class CopyColorAsTaskBase : IPipelineTask
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    /// <summary>Format the colour into a clipboard string. <paramref name="config"/> is the
    /// per-step JSON the user set in the workflow editor — most subclasses ignore it; Hex uses
    /// it to toggle the leading "#". Implementations are pure functions of the inputs (no IO)
    /// so they can be unit-tested in isolation.</summary>
    public abstract string Format(ShapeColor c, JsonNode? config);

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        if (!context.Bag.TryGetValue(PipelineBagKeys.Color, out var raw) || raw is not ShapeColor c)
        {
            // No colour from upstream — abort instead of silently writing something nonsensical.
            // The sampler/picker tasks abort the pipeline when cancelled, so reaching here means
            // there's a wiring bug (Copy step without a producer step before it).
            context.Abort($"{Id}: no color in bag — add a Color sampler or Color picker step first");
            return Task.CompletedTask;
        }
        var text = Format(c, config);
        Application.Current.Dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch { /* clipboard locked momentarily — let the next step / next run try again */ }
        });
        return Task.CompletedTask;
    }

    // ── Helpers shared by concrete subclasses ──────────────────────────────────────────

    protected static (double C, double M, double Y, double K) RgbToCmyk(ShapeColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var k = 1.0 - Math.Max(r, Math.Max(g, b));
        if (k >= 0.99999) return (0, 0, 0, 100);
        return (((1 - r - k) / (1 - k)) * 100,
                ((1 - g - k) / (1 - k)) * 100,
                ((1 - b - k) / (1 - k)) * 100,
                k * 100);
    }

    protected static (double H, double S, double V) RgbToHsb(ShapeColor c)
    {
        var hsv = Hsv.FromRgb(c.R, c.G, c.B);
        return (hsv.H * 360, hsv.S * 100, hsv.V * 100);
    }
}

public sealed class CopyColorAsHexTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-hex";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as HEX";

    /// <summary>Config schema: <c>{"alpha": bool, "hash": bool}</c>.
    /// Output format is <b>RRGGBB</b> (web/CSS convention) by default; with
    /// <c>"alpha":true</c> the alpha byte is appended for <b>RRGGBBAA</b>. With
    /// <c>"hash":true</c> the result is prefixed with <c>#</c>. Defaults to no alpha + no hash:
    /// matches what most game engines, design tools and command-line colour utilities expect.
    /// (Note: WPF's own <c>#AARRGGBB</c> ordering is the OUTLIER among the wider ecosystem.)</summary>
    public override string Format(ShapeColor c, JsonNode? config)
    {
        var includeAlpha = config?["alpha"]?.GetValue<bool>() ?? false;
        var includeHash  = config?["hash"]?.GetValue<bool>()  ?? false;
        var prefix = includeHash ? "#" : string.Empty;
        return includeAlpha
            ? $"{prefix}{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}"
            : $"{prefix}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}

public sealed class CopyColorAsRgbTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-rgb";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as RGB";
    public override string Format(ShapeColor c, JsonNode? config) => $"rgb({c.R}, {c.G}, {c.B})";
}

public sealed class CopyColorAsRgbaTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-rgba";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as RGBA";
    public override string Format(ShapeColor c, JsonNode? config) =>
        $"rgba({c.R}, {c.G}, {c.B}, {(c.A / 255.0).ToString("0.##", CultureInfo.InvariantCulture)})";
}

public sealed class CopyColorAsHsbTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-hsb";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as HSB";
    public override string Format(ShapeColor c, JsonNode? config)
    {
        var (h, s, v) = RgbToHsb(c);
        return $"hsb({(int)Math.Round(h)}°, {(int)Math.Round(s)}%, {(int)Math.Round(v)}%)";
    }
}

public sealed class CopyColorAsCmykTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-cmyk";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as CMYK";
    public override string Format(ShapeColor c, JsonNode? config)
    {
        var (cy, m, y, k) = RgbToCmyk(c);
        return $"cmyk({cy.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{m.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{y.ToString("0.#", CultureInfo.InvariantCulture)}%, " +
               $"{k.ToString("0.#", CultureInfo.InvariantCulture)}%)";
    }
}

public sealed class CopyColorAsDecimalTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-decimal";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as Decimal";
    public override string Format(ShapeColor c, JsonNode? config)
        => (((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B)
            .ToString(CultureInfo.InvariantCulture);
}

public sealed class CopyColorAsLinearTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-linear";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as Linear (UE FLinearColor)";
    public override string Format(ShapeColor c, JsonNode? config)
    {
        string F(byte ch) => (ch / 255.0).ToString("0.000000", CultureInfo.InvariantCulture);
        return $"(R={F(c.R)},G={F(c.G)},B={F(c.B)},A={F(c.A)})";
    }
}

public sealed class CopyColorAsBgraTask : CopyColorAsTaskBase
{
    public const string TaskId = "arestoys.copy-color-bgra";
    public override string Id => TaskId;
    public override string DisplayName => "Copy color as BGRA (UE FColor)";
    public override string Format(ShapeColor c, JsonNode? config) => $"(B={c.B},G={c.G},R={c.R},A={c.A})";
}
