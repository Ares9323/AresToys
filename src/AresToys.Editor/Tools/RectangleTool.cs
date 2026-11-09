using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

public sealed class RectangleTool : IDrawingTool
{
    private double _startX, _startY;
    private ShapeColor _outline = ShapeColor.Red, _fill = ShapeColor.Transparent;
    private double _strokeWidth = 2;
    private bool _active;

    public EditorTool Kind => EditorTool.Rectangle;
    public Shape? PreviewShape { get; private set; }

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _startX = x; _startY = y;
        _outline = outline; _fill = fill;
        _strokeWidth = strokeWidth;
        _active = true;
        PreviewShape = BuildShape(x, y);
    }

    public void Update(double x, double y)
    {
        if (!_active) return;
        PreviewShape = BuildShape(x, y);
    }

    public Shape? Commit(double x, double y)
    {
        if (!_active) return null;
        var final = BuildShape(x, y);
        _active = false;
        PreviewShape = null;
        return final is RectangleShape r && !r.IsEmpty ? final : null;
    }

    private RectangleShape BuildShape(double x, double y)
    {
        var left = Math.Min(_startX, x);
        var top = Math.Min(_startY, y);
        var width = Math.Abs(x - _startX);
        var height = Math.Abs(y - _startY);
        return new RectangleShape(left, top, width, height, _outline, _fill, _strokeWidth);
    }
}
