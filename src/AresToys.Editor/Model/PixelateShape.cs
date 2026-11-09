namespace AresToys.Editor.Model;

/// <summary>Rect-region pixelation. BlockSize is the side length of each mosaic cell (in pixels).</summary>
public sealed record PixelateShape(
    double X,
    double Y,
    double Width,
    double Height,
    int BlockSize)
    : Shape(ShapeColor.Transparent, ShapeColor.Transparent, 0)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
