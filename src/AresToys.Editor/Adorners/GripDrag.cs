using AresToys.Editor.Model;

namespace AresToys.Editor.Adorners;

public static class GripDrag
{
    /// <summary>Compute the shape produced by dragging <paramref name="grip"/> on <paramref name="start"/>
    /// to point (<paramref name="px"/>, <paramref name="py"/>). Returns null if the grip / shape pair
    /// is unsupported.</summary>
    /// <param name="shiftHeld">When true: rect/ellipse preserve aspect ratio; line/arrow snap to 45°;
    /// text/stepcounter behave the same as without shift.</param>
    public static Shape? Transform(Shape start, GripKind grip, double px, double py, bool shiftHeld) => start switch
    {
        RectangleShape r when grip == GripKind.Rotate => r with { Rotation = AngleFromPivot(r.X + r.Width / 2, r.Y + r.Height / 2, px, py, shiftHeld) },
        EllipseShape e when grip == GripKind.Rotate => e with { Rotation = AngleFromPivot(e.X + e.Width / 2, e.Y + e.Height / 2, px, py, shiftHeld) },
        TextShape txt when grip == GripKind.Rotate => txt with { Rotation = AngleFromPivot(txt.X + txt.Width / 2, txt.Y + txt.Height / 2, px, py, shiftHeld) },
        BlurShape b => ResizeRect(new RectShim(b.X, b.Y, b.Width, b.Height), grip, px, py, shiftHeld) is { } box1
            ? b with { X = box1.X, Y = box1.Y, Width = box1.W, Height = box1.H }
            : null,
        PixelateShape p => ResizeRect(new RectShim(p.X, p.Y, p.Width, p.Height), grip, px, py, shiftHeld) is { } box2
            ? p with { X = box2.X, Y = box2.Y, Width = box2.W, Height = box2.H }
            : null,
        SpotlightShape sp => ResizeRect(new RectShim(sp.X, sp.Y, sp.Width, sp.Height), grip, px, py, shiftHeld) is { } box3
            ? sp with { X = box3.X, Y = box3.Y, Width = box3.W, Height = box3.H }
            : null,
        ImageShape i when grip == GripKind.Rotate => i with { Rotation = AngleFromPivot(i.X + i.Width / 2, i.Y + i.Height / 2, px, py, shiftHeld) },
        // ImageShape's Shift preserves the *original* aspect ratio (W/H), not a square.
        ImageShape i => ResizeRect(new RectShim(i.X, i.Y, i.Width, i.Height), grip, px, py, shiftHeld, aspectRatio: i.Height > 0 ? i.Width / i.Height : 1) is { } box4
            ? i with { X = box4.X, Y = box4.Y, Width = box4.W, Height = box4.H }
            : null,
        SmartEraserShape se => ResizeRect(new RectShim(se.X, se.Y, se.Width, se.Height), grip, px, py, shiftHeld) is { } box5
            ? se with { X = box5.X, Y = box5.Y, Width = box5.W, Height = box5.H }
            : null,
        RectangleShape r => ResizeRect(new RectShim(r.X, r.Y, r.Width, r.Height), grip, px, py, shiftHeld) is { } box
            ? r with { X = box.X, Y = box.Y, Width = box.W, Height = box.H }
            : null,
        EllipseShape e => ResizeRect(new RectShim(e.X, e.Y, e.Width, e.Height), grip, px, py, shiftHeld) is { } box
            ? e with { X = box.X, Y = box.Y, Width = box.W, Height = box.H }
            : null,
        LineShape l => grip switch
        {
            GripKind.From => SnapEndpoint(l.ToX, l.ToY, px, py, shiftHeld) is var (fx, fy)
                ? l with { FromX = fx, FromY = fy }
                : null,
            GripKind.To => SnapEndpoint(l.FromX, l.FromY, px, py, shiftHeld) is var (tx, ty)
                ? l with { ToX = tx, ToY = ty }
                : null,
            // Drag the Bend grip → set ControlOffset relative to the From-To midpoint. Shift snaps
            // to the perpendicular bisector so the bend stays symmetric.
            GripKind.Bend => l with { ControlOffsetX = px - (l.FromX + l.ToX) / 2, ControlOffsetY = py - (l.FromY + l.ToY) / 2 },
            _ => null
        },
        TextShape t => ResizeRect(new RectShim(t.X, t.Y, t.Width, t.Height), grip, px, py, shiftHeld) is { } textBox
            ? t with { X = textBox.X, Y = textBox.Y, Width = textBox.W, Height = textBox.H }
            : null,
        StepCounterShape c => grip switch
        {
            GripKind.Resize => ResizeStepCounter(c, px, py),
            // Tail drag: the dragged point becomes the new TailX/TailY directly (no snapping,
            // no perpendicular projection — ShareX parity). IsTailActive flips to true on the
            // first drag so the wedge starts rendering even if the user happens to drop the
            // tail still inside the disc (we hide it via IsTailVisible in that case, and the
            // moment they drag it back out the wedge re-appears without a separate flag).
            GripKind.Tail => c with { TailX = px, TailY = py, IsTailActive = true },
            _ => null,
        },
        _ => null
    };

    /// <summary>Compute the new (X, Y, Width, Height) for a resized rectangle. The shape interface
    /// is shared via <see cref="RectShim"/>. <paramref name="aspectRatio"/> (W/H) drives Shift behavior:
    /// <c>1</c> = square (rectangle/ellipse default), any other positive value = preserve that aspect
    /// (used by ImageShape to lock to its original ratio).</summary>
    private static (double X, double Y, double W, double H)? ResizeRect(IRectLike s, GripKind grip, double px, double py, bool shiftHeld, double aspectRatio = 1)
    {
        // Identify the "fixed corner" — the diagonal opposite of the dragged grip — for resizing.
        double fixedX, fixedY;
        bool freeX = true, freeY = true;
        switch (grip)
        {
            case GripKind.TopLeft: fixedX = s.X + s.Width; fixedY = s.Y + s.Height; break;
            case GripKind.TopRight: fixedX = s.X; fixedY = s.Y + s.Height; break;
            case GripKind.BottomLeft: fixedX = s.X + s.Width; fixedY = s.Y; break;
            case GripKind.BottomRight: fixedX = s.X; fixedY = s.Y; break;
            case GripKind.Top: fixedX = s.X; fixedY = s.Y + s.Height; freeX = false; break;
            case GripKind.Bottom: fixedX = s.X; fixedY = s.Y; freeX = false; break;
            case GripKind.Left: fixedX = s.X + s.Width; fixedY = s.Y; freeY = false; break;
            case GripKind.Right: fixedX = s.X; fixedY = s.Y; freeY = false; break;
            default: return null;
        }

        var newX = freeX ? Math.Min(fixedX, px) : s.X;
        var newY = freeY ? Math.Min(fixedY, py) : s.Y;
        var newW = freeX ? Math.Abs(px - fixedX) : s.Width;
        var newH = freeY ? Math.Abs(py - fixedY) : s.Height;

        if (shiftHeld && freeX && freeY && aspectRatio > 0)
        {
            // Lock to the supplied aspect ratio (W/H). Pick whichever dimension is "more dragged"
            // and derive the other; the fixed corner stays put, the rest expands toward the drag.
            double constrainedW, constrainedH;
            if (newW / aspectRatio >= newH)
            {
                constrainedW = newW;
                constrainedH = newW / aspectRatio;
            }
            else
            {
                constrainedH = newH;
                constrainedW = newH * aspectRatio;
            }
            var dirX = Math.Sign(px - fixedX); if (dirX == 0) dirX = 1;
            var dirY = Math.Sign(py - fixedY); if (dirY == 0) dirY = 1;
            newX = dirX > 0 ? fixedX : fixedX - constrainedW;
            newY = dirY > 0 ? fixedY : fixedY - constrainedH;
            newW = constrainedW;
            newH = constrainedH;
        }

        // Clamp to a minimum so we don't end up with degenerate zero-size shapes.
        if (newW < 1) newW = 1;
        if (newH < 1) newH = 1;
        return (newX, newY, newW, newH);
    }

    /// <summary>The Rotate grip lives ABOVE the shape (negative Y from pivot) when Rotation == 0,
    /// so we offset the raw atan2 by 90° to get a clean 0° = "no rotation". Shift snaps to 15° steps.</summary>
    private static double AngleFromPivot(double cx, double cy, double px, double py, bool shiftHeld)
    {
        var dx = px - cx;
        var dy = py - cy;
        if (dx == 0 && dy == 0) return 0;
        var deg = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;
        if (shiftHeld) deg = Math.Round(deg / 15.0) * 15.0;
        // Normalize to [-180, 180): more intuitive than [0, 360) — left = negative, right = positive.
        deg = ((deg + 180) % 360 + 360) % 360 - 180;
        return deg;
    }

    private static (double X, double Y) SnapEndpoint(double anchorX, double anchorY, double px, double py, bool shiftHeld)
    {
        if (!shiftHeld) return (px, py);
        var dx = px - anchorX;
        var dy = py - anchorY;
        var angle = Math.Atan2(dy, dx);
        var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        var len = Math.Sqrt(dx * dx + dy * dy);
        return (anchorX + len * Math.Cos(snapped), anchorY + len * Math.Sin(snapped));
    }

    private static StepCounterShape ResizeStepCounter(StepCounterShape c, double px, double py)
    {
        var dx = px - c.CenterX;
        var dy = py - c.CenterY;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        // Grip sits at radius * sqrt(2)/2 ≈ radius * 0.707; invert to recover radius from drag distance.
        // Radius is derived from StrokeWidth*2.5 inside StepCounterShape, so we drive the stroke
        // to scale the disc — `c with { Radius = ... }` is no longer settable.
        var radius = Math.Clamp(dist / 0.707, 6, 200);
        return c with { StrokeWidth = radius / 2.5 };
    }

    private interface IRectLike
    {
        double X { get; }
        double Y { get; }
        double Width { get; }
        double Height { get; }
    }

    private sealed record RectShim(double X, double Y, double Width, double Height) : IRectLike;
}
