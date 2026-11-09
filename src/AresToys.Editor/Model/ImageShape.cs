namespace AresToys.Editor.Model;

/// <summary>Rectangular bitmap pasted onto the canvas. Carries its own PNG bytes inline so undo/redo
/// of the surrounding command stack works without external storage.</summary>
public sealed record ImageShape(
    double X,
    double Y,
    double Width,
    double Height,
    byte[] PngBytes,
    double Rotation = 0)
    : Shape(ShapeColor.Transparent, ShapeColor.Transparent, 0)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || PngBytes.Length == 0;
}
