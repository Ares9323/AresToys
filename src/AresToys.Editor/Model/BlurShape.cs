namespace AresToys.Editor.Model;

/// <summary>Rect-region gaussian blur. The shape carries no color of its own — outline/fill are
/// satisfied for Shape's contract but unused at render time. Radius is in pixels.</summary>
public sealed record BlurShape(
    double X,
    double Y,
    double Width,
    double Height,
    double Radius)
    : Shape(ShapeColor.Transparent, ShapeColor.Transparent, 0)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
