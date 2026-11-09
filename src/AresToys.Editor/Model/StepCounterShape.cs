namespace AresToys.Editor.Model;

public sealed record StepCounterShape(
    double CenterX,
    double CenterY,
    int Number,
    ShapeColor Outline,
    ShapeColor Fill,
    double StrokeWidth,
    string FontFamily = "Segoe UI",
    bool Bold = true,
    bool Italic = false,
    double TailX = 0,
    double TailY = 0,
    bool IsTailActive = false)
    : Shape(Outline, Fill, StrokeWidth)
{
    /// <summary>Disc radius derived from <see cref="Shape.StrokeWidth"/>. Step counters used
    /// to carry an independent <c>Radius</c> field, but the user wanted size to ride on the
    /// existing stroke knob — that way the mouse-wheel utility (which adjusts stroke) and the
    /// "save as default" persistence (which already covers stroke) both work for free.
    /// Multiplier is 2.5 (not 10): at the slider's max stroke of 20 a step counter sits at
    /// radius 50, which matches the visual weight of an arrow with stroke ≈5 — the user's
    /// calibration point. *10 was too aggressive and produced 200-px discs at max stroke.</summary>
    public double Radius => StrokeWidth * 2.5;

    /// <summary>True when the tail tip is outside the disc and should be rendered as a
    /// triangular wedge pointing from the center to <see cref="TailX"/>/<see cref="TailY"/>.
    /// ShareX-parity behaviour: the tail only paints when the user has dragged the tail
    /// handle clear of the disc (otherwise the wedge would just collapse into the disc).
    /// <see cref="IsTailActive"/> guards the no-tail-yet case (a freshly placed counter with
    /// the handle still parked at the default offset doesn't render the wedge).</summary>
    public bool IsTailVisible
    {
        get
        {
            if (!IsTailActive) return false;
            var dx = TailX - CenterX;
            var dy = TailY - CenterY;
            return (dx * dx + dy * dy) > (Radius * Radius);
        }
    }
}
