using AresToys.Editor.Model;

namespace AresToys.Editor.HitTesting;

public static class ShapeHitTester
{
    private const double Tolerance = 4.0;
    private const double RegionTolerance = 6.0;

    /// <summary>Grab zone around an outline, in screen pixels. Callers divide by the view zoom to get
    /// the canvas-unit tolerance they pass in, so the zone stays the same size on screen at any zoom.</summary>
    public const double ScreenTolerance = 8.0;

    /// <summary>Returns the topmost shape under the point, or null. Iterates back-to-front (last = top).
    /// <paramref name="tolerance"/> is the grab zone around outlines in canvas units; null keeps each
    /// shape kind's own default.</summary>
    public static Shape? HitTest(IReadOnlyList<Shape> shapes, double px, double py, double? tolerance = null)
    {
        for (var i = shapes.Count - 1; i >= 0; i--)
        {
            if (IsHit(shapes[i], px, py, tolerance)) return shapes[i];
        }
        return null;
    }

    public static bool IsHit(Shape shape, double px, double py, double? tolerance = null) => shape switch
    {
        RectangleShape r => HitRect(r, UnrotateAroundCenter(px, py, r.X + r.Width / 2, r.Y + r.Height / 2, r.Rotation), tolerance ?? Tolerance),
        EllipseShape e => HitEllipse(e, UnrotateAroundCenter(px, py, e.X + e.Width / 2, e.Y + e.Height / 2, e.Rotation), tolerance ?? Tolerance),
        LineShape l => HitArrowOrLineRotated(l.FromX, l.FromY, l.ControlPoint.X, l.ControlPoint.Y, l.ToX, l.ToY, l.StrokeWidth, l.Rotation, l.Midpoint, px, py, tolerance ?? Tolerance),
        FreehandShape f => HitFreehandRotated(f, px, py, tolerance ?? Tolerance),
        TextShape t => HitTextRotated(t, px, py),
        StepCounterShape c => HitStepCounter(c, px, py),
        BlurShape b => HitRectRegion(b.X, b.Y, b.Width, b.Height, px, py, tolerance ?? RegionTolerance),
        PixelateShape p => HitRectRegion(p.X, p.Y, p.Width, p.Height, px, py, tolerance ?? RegionTolerance),
        SpotlightShape s => HitRectRegion(s.X, s.Y, s.Width, s.Height, px, py, tolerance ?? RegionTolerance),
        // Image: rotate-aware solid rect (whole interior is hittable, unlike effect shapes).
        ImageShape i => HitImageRotated(i, px, py),
        // Smart eraser: solid rect (whole interior is hittable, not just border) since the gradient
        // fill replaces the underlying pixels visually.
        SmartEraserShape se => px >= se.X && px <= se.X + se.Width && py >= se.Y && py <= se.Y + se.Height,
        _ => false
    };

    private static bool HitImageRotated(ImageShape i, double px, double py)
    {
        var cx = i.X + i.Width / 2;
        var cy = i.Y + i.Height / 2;
        var (qx, qy) = UnrotateAroundCenter(px, py, cx, cy, i.Rotation);
        return qx >= i.X && qx <= i.X + i.Width && qy >= i.Y && qy <= i.Y + i.Height;
    }

    /// <summary>Hit the perimeter of a rect region (for effect shapes whose interior is "see-through"
    /// in editing terms — clicking the middle should not block clicks on shapes underneath).</summary>
    private static bool HitRectRegion(double x, double y, double w, double h, double px, double py, double Tol)
    {
        // Generous hit zone on the border so users can grab a thin frame easily.
        if (px < x - Tol || px > x + w + Tol || py < y - Tol || py > y + h + Tol) return false;
        var distLeft = Math.Abs(px - x);
        var distRight = Math.Abs(px - (x + w));
        var distTop = Math.Abs(py - y);
        var distBottom = Math.Abs(py - (y + h));
        var insideX = px >= x && px <= x + w;
        var insideY = py >= y && py <= y + h;
        return (insideY && (distLeft < Tol || distRight < Tol))
            || (insideX && (distTop < Tol || distBottom < Tol));
    }

    /// <summary>Rotate <paramref name="px"/>,<paramref name="py"/> by -<paramref name="degrees"/> around
    /// (<paramref name="cx"/>,<paramref name="cy"/>). Used to hit-test a rotated shape against its
    /// non-rotated geometry.</summary>
    public static (double X, double Y) UnrotateAroundCenter(double px, double py, double cx, double cy, double degrees)
    {
        if (degrees == 0) return (px, py);
        var rad = -degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = px - cx;
        var dy = py - cy;
        return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    private static bool HitRect(RectangleShape r, (double X, double Y) p, double tolerance) => HitRect(r, p.X, p.Y, tolerance);

    private static bool HitEllipse(EllipseShape e, (double X, double Y) p, double tolerance) => HitEllipse(e, p.X, p.Y, tolerance);

    private static bool HitTextRotated(TextShape t, double px, double py)
    {
        // Box-driven hit-test now: TextShape carries explicit Width/Height that the user can
        // resize via grips. The whole rect is hittable so dragging on the empty area inside
        // the box still grabs the text — matches Photoshop / Figma's "click anywhere in the
        // text frame" behaviour. Rotation is unwound around the box centre.
        var cx = t.X + t.Width / 2;
        var cy = t.Y + t.Height / 2;
        var (qx, qy) = UnrotateAroundCenter(px, py, cx, cy, t.Rotation);
        return qx >= t.X && qx <= t.X + t.Width && qy >= t.Y && qy <= t.Y + t.Height;
    }

    private static bool HitStepCounter(StepCounterShape c, double px, double py)
    {
        var dx = px - c.CenterX;
        var dy = py - c.CenterY;
        return dx * dx + dy * dy <= c.Radius * c.Radius;
    }

    private static bool HitRect(RectangleShape r, double x, double y, double tolerance)
    {
        if (!r.Fill.IsTransparent)
            return x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height;

        // A fully transparent fill is see-through: the middle must not grab the shape or it would
        // block whatever lies beneath. Only the outline band is hittable, inside or outside it, and
        // it stays hittable even when the outline itself is thin or invisible.
        var inX = x >= r.X && x <= r.X + r.Width;
        var inY = y >= r.Y && y <= r.Y + r.Height;
        double dist;
        if (inX && inY)
            dist = Math.Min(Math.Min(x - r.X, r.X + r.Width - x), Math.Min(y - r.Y, r.Y + r.Height - y));
        else
        {
            var dx = Math.Max(Math.Max(r.X - x, 0), x - (r.X + r.Width));
            var dy = Math.Max(Math.Max(r.Y - y, 0), y - (r.Y + r.Height));
            dist = Math.Sqrt(dx * dx + dy * dy);
        }
        return dist <= tolerance + r.StrokeWidth / 2;
    }

    private static bool HitEllipse(EllipseShape e, double x, double y, double tolerance)
    {
        var cx = e.X + e.Width / 2.0;
        var cy = e.Y + e.Height / 2.0;
        var rx = e.Width / 2.0;
        var ry = e.Height / 2.0;
        if (rx <= 0 || ry <= 0) return false;
        var nx = (x - cx) / rx;
        var ny = (y - cy) / ry;
        var f = nx * nx + ny * ny - 1.0;
        if (!e.Fill.IsTransparent) return f <= 0.0;

        // Transparent fill: only the band around the curve. Distance to the curve is approximated
        // by f / |grad f| (first order, exact enough at a few pixels from the outline).
        var gx = 2 * nx / rx;
        var gy = 2 * ny / ry;
        var grad = Math.Sqrt(gx * gx + gy * gy);
        if (grad < 1e-9) return Math.Min(rx, ry) <= tolerance + e.StrokeWidth / 2;
        return Math.Abs(f) / grad <= tolerance + e.StrokeWidth / 2;
    }

    private static bool HitSegment(double x1, double y1, double x2, double y2, double strokeWidth, double px, double py, double tolerance)
    {
        var dx = x2 - x1; var dy = y2 - y1;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6) return false;
        var t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lenSq, 0, 1);
        var nearestX = x1 + t * dx;
        var nearestY = y1 + t * dy;
        var distSq = (px - nearestX) * (px - nearestX) + (py - nearestY) * (py - nearestY);
        var tol = Math.Max(tolerance, strokeWidth / 2 + 2);
        return distSq <= tol * tol;
    }

    /// <summary>Hit a quadratic bezier From → Control → To by sampling 32 line segments along
    /// the curve and reusing <see cref="HitSegment"/>. 32 samples is plenty for the curvature
    /// arrows/lines reach in practice and stays cheap (called per shape on every mouse move).
    /// When Control coincides with the midpoint the bezier reduces to the straight line, so the
    /// uncurved case still matches the previous segment-based behaviour.</summary>
    private static bool HitBezier(double x0, double y0, double cx, double cy, double x2, double y2, double strokeWidth, double px, double py, double tolerance)
    {
        const int Samples = 32;
        var prevX = x0;
        var prevY = y0;
        for (var i = 1; i <= Samples; i++)
        {
            var t = i / (double)Samples;
            var omt = 1.0 - t;
            // B(t) = (1-t)^2 * P0 + 2(1-t)t * P1 + t^2 * P2
            var nextX = omt * omt * x0 + 2 * omt * t * cx + t * t * x2;
            var nextY = omt * omt * y0 + 2 * omt * t * cy + t * t * y2;
            if (HitSegment(prevX, prevY, nextX, nextY, strokeWidth, px, py, tolerance)) return true;
            prevX = nextX;
            prevY = nextY;
        }
        return false;
    }

    private static bool HitFreehand(FreehandShape f, double x, double y, double tolerance)
    {
        for (var i = 0; i < f.Points.Count - 1; i++)
        {
            var (x1, y1) = f.Points[i];
            var (x2, y2) = f.Points[i + 1];
            if (HitSegment(x1, y1, x2, y2, f.StrokeWidth, x, y, tolerance)) return true;
        }
        return false;
    }

    /// <summary>Hit-test a rotated arrow/line by unrotating the click around the segment midpoint
    /// before sampling the bezier. Bezier coords are stored in unrotated space; the rotation is a
    /// pure render transform.</summary>
    private static bool HitArrowOrLineRotated(double x0, double y0, double cx, double cy, double x2, double y2,
        double strokeWidth, double rotation, (double X, double Y) midpoint, double px, double py, double tolerance)
    {
        var (qx, qy) = UnrotateAroundCenter(px, py, midpoint.X, midpoint.Y, rotation);
        return HitBezier(x0, y0, cx, cy, x2, y2, strokeWidth, qx, qy, tolerance);
    }

    private static bool HitFreehandRotated(FreehandShape f, double px, double py, double tolerance)
    {
        if (f.Rotation == 0) return HitFreehand(f, px, py, tolerance);
        var (cx, cy) = f.Pivot;
        var (qx, qy) = UnrotateAroundCenter(px, py, cx, cy, f.Rotation);
        return HitFreehand(f, qx, qy, tolerance);
    }
}
