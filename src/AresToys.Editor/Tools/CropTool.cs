using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

/// <summary>Drag-rect tool. Doesn't add a shape on commit — instead exposes <see cref="LastRect"/>
/// so the host can apply a destructive crop.</summary>
public sealed class CropTool : IDrawingTool
{
    private double _startX, _startY;
    private bool _active;

    public EditorTool Kind => EditorTool.Crop;
    public Shape? PreviewShape { get; private set; }

    /// <summary>Set on Commit: rect (X, Y, Width, Height) the user dragged. Null until first commit.</summary>
    public (double X, double Y, double Width, double Height)? LastRect { get; private set; }

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _startX = x; _startY = y;
        _active = true;
        LastRect = null;
        PreviewShape = BuildPreview(x, y);
    }

    public void Update(double x, double y) { if (_active) PreviewShape = BuildPreview(x, y); }

    public Shape? Commit(double x, double y)
    {
        if (!_active) return null;
        _active = false;
        PreviewShape = null;
        var left = Math.Min(_startX, x);
        var top = Math.Min(_startY, y);
        var w = Math.Abs(x - _startX);
        var h = Math.Abs(y - _startY);
        if (w < 1 || h < 1) { LastRect = null; return null; }
        LastRect = (left, top, w, h);
        return null; // crop is applied by the host via VM.ApplyCrop, not as an AddShapeCommand.
    }

    private RectangleShape BuildPreview(double x, double y)
    {
        var left = Math.Min(_startX, x);
        var top = Math.Min(_startY, y);
        var width = Math.Abs(x - _startX);
        var height = Math.Abs(y - _startY);
        // Use a dashed-style rect for the preview by reusing RectangleShape.
        return new RectangleShape(left, top, width, height, ShapeColor.Red, ShapeColor.Transparent, 2);
    }
}
