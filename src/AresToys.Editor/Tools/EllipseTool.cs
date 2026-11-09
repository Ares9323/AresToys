using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

public sealed class EllipseTool : IDrawingTool
{
    private double _startX, _startY;
    private ShapeColor _outline = ShapeColor.Red, _fill = ShapeColor.Transparent;
    private double _strokeWidth = 2;
    private bool _active;

    public EditorTool Kind => EditorTool.Ellipse;
    public Shape? PreviewShape { get; private set; }

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _startX = x; _startY = y;
        _outline = outline; _fill = fill; _strokeWidth = strokeWidth;
        _active = true;
        PreviewShape = Build(x, y);
    }

    public void Update(double x, double y)
    {
        if (!_active) return;
        PreviewShape = Build(x, y);
    }

    public Shape? Commit(double x, double y)
    {
        if (!_active) return null;
        _active = false;
        PreviewShape = null;
        var s = Build(x, y);
        return s.IsEmpty ? null : s;
    }

    private EllipseShape Build(double x, double y) => new(
        Math.Min(_startX, x), Math.Min(_startY, y),
        Math.Abs(x - _startX), Math.Abs(y - _startY),
        _outline, _fill, _strokeWidth);
}
