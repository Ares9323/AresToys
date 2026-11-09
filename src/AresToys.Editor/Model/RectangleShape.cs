namespace AresToys.Editor.Model;

public sealed record RectangleShape(
    double X,
    double Y,
    double Width,
    double Height,
    ShapeColor Outline,
    ShapeColor Fill,
    double StrokeWidth,
    double Rotation = 0)
    : Shape(Outline, Fill, StrokeWidth)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
