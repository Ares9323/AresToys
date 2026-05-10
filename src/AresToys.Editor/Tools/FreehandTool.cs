using AresToys.Editor.Model;

namespace AresToys.Editor.Tools;

public sealed class FreehandTool : IDrawingTool
{
    private readonly List<(double X, double Y)> _points = [];
    private ShapeColor _outline = ShapeColor.Red;
    private double _strokeWidth = 2;
    private bool _active;

    public EditorTool Kind => EditorTool.Freehand;
    public Shape? PreviewShape { get; private set; }

    /// <summary>Sticky default applied to new strokes — propagated by <c>EditorViewModel</c> from
    /// the persisted <c>EditorDefaults</c>. Toggling the per-shape Smooth flag in the properties
    /// panel also updates this so the next stroke inherits the same choice.</summary>
    public bool SmoothStrokes { get; set; } = true;

    /// <summary>Sticky cap defaults for ShareX-style freehand arrows. Same propagation flow as
    /// <see cref="SmoothStrokes"/>: per-stroke overrides in the properties panel write back here
    /// so subsequent strokes inherit them.</summary>
    public bool StartCap { get; set; }
    public bool EndCap { get; set; }
    public LineTipStyle TipStyle { get; set; } = LineTipStyle.ShareXCurve;

    public void Begin(double x, double y, ShapeColor outline, ShapeColor fill, double strokeWidth)
    {
        _points.Clear();
        _points.Add((x, y));
        _outline = outline; _strokeWidth = strokeWidth;
        _active = true;
        PreviewShape = MakeShape();
    }

    public void Update(double x, double y)
    {
        if (!_active) return;
        var last = _points[^1];
        if (Math.Abs(last.X - x) < 0.5 && Math.Abs(last.Y - y) < 0.5) return;
        _points.Add((x, y));
        PreviewShape = MakeShape();
    }

    public Shape? Commit(double x, double y)
    {
        if (!_active) return null;
        _active = false;
        if (_points.Count < 2) { PreviewShape = null; return null; }
        var shape = MakeShape();
        PreviewShape = null;
        return shape;
    }

    private FreehandShape MakeShape() =>
        new([.. _points], _outline, _strokeWidth,
            Smooth: SmoothStrokes, StartCap: StartCap, EndCap: EndCap, TipStyle: TipStyle);
}
