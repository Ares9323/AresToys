using SkiaSharp;

namespace AresToys.ImageEffects.Drawing;

/// <summary>Linear-gradient definition: a direction plus an ordered list of stops. The JSON
/// shape mirrors ShareX's <c>GradientInfo</c> exactly so <c>.sxie</c> presets that include
/// gradient backgrounds (e.g. <c>BackgroundGradient.sxie</c>) round-trip cleanly.</summary>
public sealed class GradientInfo
{
    public LinearGradientMode Type { get; set; } = LinearGradientMode.Vertical;
    public List<GradientStop> Colors { get; set; } = new();

    public bool IsValid => Colors is { Count: > 0 };

    public GradientInfo() { }
    public GradientInfo(LinearGradientMode type, params GradientStop[] stops)
    {
        Type = type;
        Colors = new List<GradientStop>(stops);
    }

    /// <summary>Compute the start/end points of the gradient inside a rectangle of the given
    /// size — Skia wants raw float points rather than the abstract direction enum.</summary>
    public (SKPoint Start, SKPoint End) Endpoints(int width, int height) => Type switch
    {
        LinearGradientMode.Horizontal => (new SKPoint(0, 0), new SKPoint(width, 0)),
        LinearGradientMode.Vertical => (new SKPoint(0, 0), new SKPoint(0, height)),
        LinearGradientMode.ForwardDiagonal => (new SKPoint(0, 0), new SKPoint(width, height)),
        LinearGradientMode.BackwardDiagonal => (new SKPoint(width, 0), new SKPoint(0, height)),
        _ => (new SKPoint(0, 0), new SKPoint(0, height)),
    };

    /// <summary>Build a Skia gradient shader sized for the given rectangle. Transparent
    /// stops have their RGB borrowed from the nearest non-transparent neighbour: ShareX
    /// presets often write fully transparent stops as <c>"0, 0, 0, 0"</c> (transparent black)
    /// because GDI+ interpolates RGB and alpha independently in straight-alpha space, so the
    /// RGB doesn't matter at alpha=0. Skia interpolates premultiplied colours, where alpha=0
    /// drags RGB toward zero too — that turned a "blue-fade-out" gradient into "blue → grey →
    /// black-out". Borrowing RGB from the neighbour reproduces the GDI+ look without touching
    /// the preset data.</summary>
    public SKShader CreateShader(int width, int height)
    {
        var (start, end) = Endpoints(width, height);
        var colors = new SKColor[Colors.Count];
        var positions = new float[Colors.Count];
        for (var i = 0; i < Colors.Count; i++)
        {
            colors[i] = Colors[i].Color;
            positions[i] = Math.Clamp(Colors[i].Location / 100f, 0f, 1f);
        }
        for (var i = 0; i < colors.Length; i++)
        {
            if (colors[i].Alpha != 0) continue;
            // Walk outward (right then left, expanding distance) looking for the nearest
            // stop whose alpha > 0; copy its RGB and keep alpha at 0.
            for (var d = 1; d < colors.Length; d++)
            {
                SKColor? picked = null;
                if (i + d < colors.Length && colors[i + d].Alpha > 0) picked = colors[i + d];
                else if (i - d >= 0 && colors[i - d].Alpha > 0) picked = colors[i - d];
                if (picked is not null)
                {
                    colors[i] = new SKColor(picked.Value.Red, picked.Value.Green, picked.Value.Blue, 0);
                    break;
                }
            }
        }
        return SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
    }
}
