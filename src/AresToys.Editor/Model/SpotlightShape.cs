namespace AresToys.Editor.Model;

/// <summary>Rect-region spotlight: dims and (optionally) blurs everything OUTSIDE the rect.
/// <see cref="DimAmount"/> in [0, 1] → alpha of the dim overlay.
/// <see cref="BlurRadius"/> in px → 0 disables blur on the surrounding area.</summary>
public sealed record SpotlightShape(
    double X,
    double Y,
    double Width,
    double Height,
    double DimAmount,
    double BlurRadius = 0)
    : Shape(ShapeColor.Transparent, ShapeColor.Transparent, 0)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
