using AresToys.ImageEffects.Drawing;
using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Drawings;

/// <summary>Where the border is painted relative to the source rectangle. Outside grows the
/// canvas; Inside paints over the source pixels.</summary>
public enum BorderType { Outside, Inside }

/// <summary>Stroke pattern for the border. Names mirror System.Drawing.Drawing2D.DashStyle so
/// ShareX <c>.sxie</c> files round-trip without a translation map.</summary>
public enum DashStyle { Solid, Dash, Dot, DashDot, DashDotDot }

/// <summary>Effect inspired by ShareX. Solid or
/// gradient border around (or inside) the source rectangle, with optional dashed stroke.</summary>
public sealed class DrawBorderImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_border";
    public override string Name => "Border";

    public BorderType Type { get; set; } = BorderType.Outside;

    [EffectParameter(1, 100, DisplayName = "Size")]
    public int Size { get; set; } = 1;

    public DashStyle DashStyle { get; set; } = DashStyle.Solid;
    public SKColor Color { get; set; } = SKColors.Black;
    public bool UseGradient { get; set; }
    public GradientInfo Gradient { get; set; } = new();

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Math.Max(1, Size);

        // Outside borders grow the canvas by `size` on every side; Inside borders paint into
        // the existing rectangle.
        var pad = Type == BorderType.Outside ? size : 0;
        var width = source.Width + (pad * 2);
        var height = source.Height + (pad * 2);

        var result = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, pad, pad);

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size,
            PathEffect = BuildDash(size),
        };
        if (UseGradient && Gradient.IsValid)
            stroke.Shader = Gradient.CreateShader(width, height);
        else
            stroke.Color = Color;

        // Stroke is centred on the path — inset by half so the stroke sits exactly on the
        // boundary instead of bleeding past it.
        var inset = size / 2f;
        var rect = Type == BorderType.Outside
            ? new SKRect(inset, inset, width - inset, height - inset)
            : new SKRect(pad + inset, pad + inset, width - pad - inset, height - pad - inset);
        canvas.DrawRect(rect, stroke);
        return result;
    }

    private SKPathEffect? BuildDash(int size) => DashStyle switch
    {
        DashStyle.Dash => SKPathEffect.CreateDash(new[] { size * 4f, size * 2f }, 0),
        DashStyle.Dot => SKPathEffect.CreateDash(new[] { size * 1f, size * 2f }, 0),
        DashStyle.DashDot => SKPathEffect.CreateDash(new[] { size * 4f, size * 2f, size * 1f, size * 2f }, 0),
        DashStyle.DashDotDot => SKPathEffect.CreateDash(new[] { size * 4f, size * 2f, size * 1f, size * 2f, size * 1f, size * 2f }, 0),
        _ => null,
    };
}
