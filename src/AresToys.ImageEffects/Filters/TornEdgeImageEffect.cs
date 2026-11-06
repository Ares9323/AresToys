using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Effect inspired by ShareX.
/// Walks each enabled side at <see cref="Range"/> intervals, jittering each point by up to
/// <see cref="Depth"/> pixels to build a closed polygon, then clips the source to that
/// polygon. <see cref="Curved"/> swaps straight segments for cubic-Bezier ones for a softer,
/// "ripped paper" look.</summary>
public sealed class TornEdgeImageEffect : FilterImageEffectBase
{
    public override string Id => "torn_edge";
    public override string Name => "Torn edge";

    [EffectParameter(1, 100, DisplayName = "Depth")]
    public int Depth { get; set; } = 20;

    [EffectParameter(1, 100, DisplayName = "Range")]
    public int Range { get; set; } = 20;

    public bool Top { get; set; } = true;
    public bool Right { get; set; } = true;
    public bool Bottom { get; set; } = true;
    public bool Left { get; set; } = true;
    public bool Curved { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Depth < 1 || Range < 1) return source.Copy();
        if (!Top && !Right && !Bottom && !Left) return source.Copy();

        var horizontalCount = source.Width / Range;
        var verticalCount = source.Height / Range;
        if (horizontalCount < 2 && verticalCount < 2) return source.Copy();

        var rand = Random.Shared;
        var points = new List<SKPoint>();

        // Walk top → right → bottom → left, each side either tearing into the canvas (when
        // enabled and the perpendicular tear count is high enough) or hugging the corner.
        if (Top && horizontalCount > 1)
        {
            var startX = Left && verticalCount > 1 ? Depth : 0;
            var endX = Right && verticalCount > 1 ? source.Width - Depth : source.Width;
            for (var x = startX; x < endX; x += Range)
                points.Add(new SKPoint(x, rand.Next(0, Depth + 1)));
        }
        else
        {
            points.Add(new SKPoint(0, 0));
            points.Add(new SKPoint(source.Width, 0));
        }

        if (Right && verticalCount > 1)
        {
            var startY = Top && horizontalCount > 1 ? Depth : 0;
            var endY = Bottom && horizontalCount > 1 ? source.Height - Depth : source.Height;
            for (var y = startY; y < endY; y += Range)
                points.Add(new SKPoint(source.Width - Depth + rand.Next(0, Depth + 1), y));
        }
        else
        {
            points.Add(new SKPoint(source.Width, 0));
            points.Add(new SKPoint(source.Width, source.Height));
        }

        if (Bottom && horizontalCount > 1)
        {
            var startX = Right && verticalCount > 1 ? source.Width - Depth : source.Width;
            var endX = Left && verticalCount > 1 ? Depth : 0;
            for (var x = startX; x >= endX; x -= Range)
                points.Add(new SKPoint(x, source.Height - Depth + rand.Next(0, Depth + 1)));
        }
        else
        {
            points.Add(new SKPoint(source.Width, source.Height));
            points.Add(new SKPoint(0, source.Height));
        }

        if (Left && verticalCount > 1)
        {
            var startY = Bottom && horizontalCount > 1 ? source.Height - Depth : source.Height;
            var endY = Top && horizontalCount > 1 ? Depth : 0;
            for (var y = startY; y >= endY; y -= Range)
                points.Add(new SKPoint(rand.Next(0, Depth + 1), y));
        }
        else
        {
            points.Add(new SKPoint(0, source.Height));
            points.Add(new SKPoint(0, 0));
        }

        // Drop consecutive duplicates so the path doesn't have zero-length edges.
        var distinct = new List<SKPoint>();
        if (points.Count > 0)
        {
            distinct.Add(points[0]);
            for (var i = 1; i < points.Count; i++)
                if (points[i] != points[i - 1]) distinct.Add(points[i]);
            if (distinct.Count > 1 && distinct[^1] == distinct[0]) distinct.RemoveAt(distinct.Count - 1);
        }

        var result = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        using var path = new SKPath();
        if (distinct.Count > 0)
        {
            path.MoveTo(distinct[0]);
            if (Curved && distinct.Count > 2)
            {
                // Quadratic spline through midpoints — ShareX's "curved" mode gives a softer,
                // wave-like tear instead of straight zig-zag.
                for (var i = 0; i < distinct.Count; i++)
                {
                    var current = distinct[i];
                    var next = distinct[(i + 1) % distinct.Count];
                    var mid = new SKPoint((current.X + next.X) / 2f, (current.Y + next.Y) / 2f);
                    path.QuadTo(current, mid);
                }
            }
            else
            {
                for (var i = 1; i < distinct.Count; i++) path.LineTo(distinct[i]);
            }
            path.Close();
        }

        // Mask + SrcIn pattern: paint the polygon mask with antialias, then composite the
        // bitmap inside. Same approach as RoundedCorners — gives clean sub-pixel edges.
        using var maskPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawPath(path, maskPaint);
        using var bitmapPaint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcIn };
        canvas.DrawBitmap(source, 0, 0, bitmapPaint);
        return result;
    }
}
