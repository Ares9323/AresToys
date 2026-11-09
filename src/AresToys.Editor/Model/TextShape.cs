namespace AresToys.Editor.Model;

/// <summary>A text label anchored at <see cref="X"/>/<see cref="Y"/> with explicit
/// <see cref="Width"/>/<see cref="Height"/>. Text wraps inside the box; resizing the box
/// reflows the content rather than scaling the glyphs (Photoshop / Figma convention).</summary>
public sealed record TextShape(
    double X,
    double Y,
    double Width,
    double Height,
    string Text,
    TextStyle Style,
    ShapeColor Outline,
    ShapeColor Fill,
    double StrokeWidth,
    double Rotation = 0)
    : Shape(Outline, Fill, StrokeWidth)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>Default box width for fresh text shapes, derived from the active font size:
    /// ~10 characters wide at the current size before wrapping kicks in. Scales with the user's
    /// font choice so a 12pt label and a 72pt headline both get a visually proportionate frame.</summary>
    public static double DefaultWidthFor(double fontSize) => fontSize * 10;

    /// <summary>Default box height — exactly one font-size unit, just enough for a single line.
    /// The user grows it via grips when they need more vertical room (multi-line / wrapping).</summary>
    public static double DefaultHeightFor(double fontSize) => fontSize;
}
