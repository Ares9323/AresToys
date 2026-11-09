namespace AresToys.Editor.Model;

/// <summary>Rect-region "smart eraser": fills the rect with a bilinear gradient of the four colors
/// sampled from the underlying image at the rect's corners. Designed to blend with backgrounds
/// without a blur (useful for hiding text on near-uniform areas).</summary>
public sealed record SmartEraserShape(
    double X,
    double Y,
    double Width,
    double Height)
    : Shape(ShapeColor.Transparent, ShapeColor.Transparent, 0)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
