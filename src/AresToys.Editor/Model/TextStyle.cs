namespace AresToys.Editor.Model;

public enum TextAlign
{
    Left,
    Center,
    Right
}

public sealed record TextStyle(
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    ShapeColor Color,
    TextAlign Align)
{
    public static readonly TextStyle Default = new("Segoe UI", 18, false, false, ShapeColor.Red, TextAlign.Left);
}
