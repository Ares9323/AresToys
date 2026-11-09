using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

/// <summary>Placeholder in the gesture pipeline. Actual editing happens via inline TextBox in the View,
/// which calls EditorViewModel.AddTextShape directly. Begin/Update/Commit are kept for consistency.</summary>
public sealed class TextTool : IDrawingTool
{
    private readonly TextStyle _style;
    private TextShape? _preview;

    public TextTool(TextStyle style)
    {
        _style = style;
    }

    public EditorTool Kind => EditorTool.Text;

    public Shape? PreviewShape => _preview;

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _preview = new TextShape(x, y,
            TextShape.DefaultWidthFor(_style.FontSize), TextShape.DefaultHeightFor(_style.FontSize),
            "", _style, outline, fill, strokeWidth);
    }

    public void Update(double x, double y) { }

    public Shape? Commit(double x, double y)
    {
        var p = _preview;
        _preview = null;
        return p is null || p.IsEmpty ? null : p;
    }
}
