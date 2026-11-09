using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

public sealed class SpotlightTool : IDrawingTool
{
    /// <summary>Opacity of the dim applied OUTSIDE the spotlight, 0..1. Sticky default, adjusted
    /// with Shift+wheel over a selected spotlight.</summary>
    public double Dim { get; set; } = 0.5;

    /// <summary>Blur applied to the dimmed surroundings, in pixels. 0 = dim only, which is what
    /// spotlights did before the blur became adjustable. Adjusted with the plain wheel.</summary>
    public double BlurRadius { get; set; }

    private double _startX, _startY;
    private bool _active;

    public EditorTool Kind => EditorTool.Spotlight;
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
        return final is SpotlightShape s && !s.IsEmpty ? final : null;
    }

    private SpotlightShape BuildShape(double x, double y)
    {
        var left = Math.Min(_startX, x);
        var top = Math.Min(_startY, y);
        var width = Math.Abs(x - _startX);
        var height = Math.Abs(y - _startY);
        return new SpotlightShape(left, top, width, height, Dim, BlurRadius);
    }
}
