namespace AresToys.Editor.Model;

/// <summary>Straight or curved line with optional rotation around the midpoint.
/// Unifies what used to be Line + Arrow: <see cref="StartCap"/>/<see cref="EndCap"/>
/// toggle a tip on either end, <see cref="TipStyle"/> picks its visual style. A line
/// with no caps is a plain line; with EndCap it's a classic arrow; with both, a
/// double-headed arrow. Same bezier model as before via
/// <see cref="ControlOffsetX"/>/<see cref="ControlOffsetY"/>.</summary>
public sealed record LineShape(
    double FromX, double FromY, double ToX, double ToY,
    ShapeColor Outline, double StrokeWidth,
    double ControlOffsetX = 0,
    double ControlOffsetY = 0,
    double Rotation = 0,
    bool StartCap = false,
    bool EndCap = false,
    LineTipStyle TipStyle = LineTipStyle.ShareXCurve)
    : Shape(Outline, ShapeColor.Transparent, StrokeWidth)
{
    public (double X, double Y) Midpoint => ((FromX + ToX) / 2, (FromY + ToY) / 2);
    public (double X, double Y) ControlPoint => (Midpoint.X + ControlOffsetX, Midpoint.Y + ControlOffsetY);
    public bool IsCurved => ControlOffsetX != 0 || ControlOffsetY != 0;
}
