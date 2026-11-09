using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

public sealed class BlurTool : IDrawingTool
{
    /// <summary>Radius every new blur region starts with, in pixels. Sticky default owned by
    /// <see cref="ViewModels.EditorViewModel.BlurRadiusDefault"/>: the wheel over a selected blur
    /// and "Set as default" both write back here, and it survives across sessions through
    /// <c>EditorDefaults</c>.</summary>
    public double Radius { get; set; } = 12;

    private double _startX, _startY;
    private bool _active;

    public EditorTool Kind => EditorTool.Blur;
    public Shape? PreviewShape { get; private set; }

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _startX = x; _startY = y;
        _active = true;
        PreviewShape = BuildShape(x, y);
    }

    public void Update(double x, double y) { if (_active) PreviewShape = BuildShape(x, y); }

    public Shape? Commit(double x, double y)
    {
        if (!_active) return null;
        var final = BuildShape(x, y);
        _active = false;
        PreviewShape = null;
        return final is BlurShape b && !b.IsEmpty ? final : null;
    }

    private BlurShape BuildShape(double x, double y)
    {
        var left = Math.Min(_startX, x);
        var top = Math.Min(_startY, y);
        var width = Math.Abs(x - _startX);
        var height = Math.Abs(y - _startY);
        return new BlurShape(left, top, width, height, Radius);
    }
}
