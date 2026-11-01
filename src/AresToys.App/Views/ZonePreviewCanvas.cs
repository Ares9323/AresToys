using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AresToys.App.Services.Zones;

namespace AresToys.App.Views;

/// <summary>Which gesture a click on the preview performs.</summary>
public enum ZoneEditTool
{
    Select,
    Split,
    Draw,
}

/// <summary>The interactive zone preview of the zone editor: draws a layout scaled to the aspect ratio of a
/// monitor work area, selects zones, drags boundary lines and shows the split guide. It never owns the layout:
/// every gesture ends in <see cref="EditCommitted"/> with a layout produced by <see cref="ZoneGridEditor"/>, and
/// the window pushes it into its <see cref="ZoneEditHistory{T}"/> and hands the new state back through
/// <see cref="SetLayout"/>. The same control also edits a canvas layout (free rectangles that may overlap): after
/// <see cref="SetCanvasLayout"/> it draws, selects, moves and resizes those zones with <see cref="ZoneCanvasEditor"/>
/// and raises <see cref="CanvasEditCommitted"/>; the layout kind that was set last decides the mode.</summary>
public sealed class ZonePreviewCanvas : FrameworkElement
{
    private const double OuterMargin = 12;
    private const double LineHitTolerance = 5;
    private const double HandleTolerance = 6;
    private const double DragThreshold = 4;
    private const int CanvasFillOpacityPercent = 55;
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly HashSet<int> _selection = [];
    private GridLayout? _layout;
    private GridLayout? _dragLayout;
    private ZoneAppearance _appearance = ZoneAppearance.Default;
    private int _workWidth = 1920;
    private int _workHeight = 1080;
    private ZoneEditTool _tool = ZoneEditTool.Select;
    private ZoneAxis _splitAxis = ZoneAxis.Vertical;
    private bool _mouseInside;
    private Point _mouse;
    private Segment? _hoverSegment;
    private Segment? _dragSegment;
    private bool _dragging;
    private int _splitZone = -1;
    private int _splitPosition;
    // The axis the current split preview was built with: Alt flips it for the gesture, so it can differ from _splitAxis.
    private ZoneAxis _splitPreviewAxis = ZoneAxis.Vertical;
    private EditResult? _splitPreview;

    // Canvas mode (free rectangles). It is active while _canvas is set; the grid fields above are then all idle.
    private enum CanvasDragKind { None, Create, Move, Resize }

    [Flags]
    private enum CanvasHandle { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private CanvasLayout? _canvas;
    // The layout shown while a move or resize drag is active (null: nothing to preview, draw _canvas).
    private CanvasLayout? _canvasPreview;
    private CanvasDragKind _canvasDrag;
    private CanvasHandle _canvasHandle;
    private int _canvasZone = -1;
    private CanvasZone? _canvasStartZone;
    private Point _canvasStartMouse;
    private Point _canvasStartPercent;
    // Where a Draw drag began (percent units).
    private int _canvasAnchorX;
    private int _canvasAnchorY;
    // False until the mouse moved DragThreshold from where it went down, so a plain click never edits anything.
    private bool _canvasDragMoved;
    // The rubber band of a draw or resize drag, and the editor's verdict on it.
    private CanvasZone? _canvasBand;
    private CanvasEditResult? _canvasResult;
    // Split tool in canvas mode: the zone under the pointer and what cutting it at the guide would give.
    private int _canvasSplitZone = -1;
    private CanvasEditResult? _canvasSplitPreview;

    private Brush _areaBrush = Brushes.Black;
    private Brush _fillBrush = Brushes.Transparent;
    private Brush _highlightBrush = Brushes.Transparent;
    private Brush _canvasFillBrush = Brushes.Transparent;
    private Pen _zonePen = new(Brushes.Gray, 1);
    private Pen _selectedPen = new(Brushes.Gray, 1);
    private Pen _guidePen = new(Brushes.White, 2);
    private Pen _refusedGuidePen = new(Brushes.OrangeRed, 2);
    private Pen _linePen = new(Brushes.White, 3);

    public ZonePreviewCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        // Focus lets Escape cancel a canvas drag (the control takes focus on mouse down in canvas mode).
        Focusable = true;
        FocusVisualStyle = null;
        RebuildBrushes();
    }

    /// <summary>Raised when a gesture produced a new layout that should be recorded in the history.</summary>
    public event EventHandler<GridLayout>? EditCommitted;

    /// <summary>Raised in canvas mode when a gesture (draw, move, resize) produced a new layout. The window records
    /// it and pushes it back through <see cref="SetCanvasLayout"/>.</summary>
    public event EventHandler<CanvasLayout>? CanvasEditCommitted;

    /// <summary>Raised when the set of selected zones changed because of a click.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A short message for the window's note line (for example why a split was refused), or null to clear it.</summary>
    public event EventHandler<string?>? Notice;

    public IReadOnlyCollection<int> Selection => _selection;

    public ZoneEditTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            UpdateHover();
            InvalidateVisual();
        }
    }

    public ZoneAxis SplitAxis
    {
        get => _splitAxis;
        set
        {
            _splitAxis = value;
            UpdateHover();
            InvalidateVisual();
        }
    }

    public ZoneAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value;
            RebuildBrushes();
            InvalidateVisual();
        }
    }

    /// <summary>The layout to draw. Selected indexes that no longer exist are dropped.</summary>
    public void SetLayout(GridLayout layout)
    {
        LeaveCanvasMode();
        _layout = layout;
        _dragLayout = null;
        if (_selection.RemoveWhere(i => i >= layout.ZoneCount) > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateHover();
        InvalidateVisual();
    }

    /// <summary>Shows a canvas layout and switches the control to canvas mode (the grid layout is cleared), or with
    /// null returns to grid mode. The selection is cleared when the zone count changed or the layout is null; an
    /// unchanged count keeps it, so the window can push every committed layout back without losing the selected
    /// zone. A drag in progress is cancelled.</summary>
    public void SetCanvasLayout(CanvasLayout? layout)
    {
        if (layout is null)
        {
            LeaveCanvasMode();
            UpdateHover();
            InvalidateVisual();
            return;
        }

        bool countChanged = _canvas is null || _canvas.ZoneCount != layout.ZoneCount;
        if (_dragging)
        {
            _dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        CancelCanvasDrag();
        _layout = null;
        _dragLayout = null;
        _dragSegment = null;
        _hoverSegment = null;
        _splitZone = -1;
        _splitPreview = null;
        _canvas = layout;
        bool selectionChanged = false;
        if (countChanged && _selection.Count > 0)
        {
            _selection.Clear();
            selectionChanged = true;
        }

        UpdateHover();
        InvalidateVisual();
        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The work area size in physical pixels: it sets the aspect ratio and the pixel numbers in labels.</summary>
    public void SetWorkArea(int widthPixels, int heightPixels)
    {
        _workWidth = Math.Max(1, widthPixels);
        _workHeight = Math.Max(1, heightPixels);
        InvalidateVisual();
    }

    /// <summary>Replaces the selection without raising <see cref="SelectionChanged"/> (the caller already knows).</summary>
    public void SetSelection(IEnumerable<int> zones)
    {
        _selection.Clear();
        foreach (int zone in zones)
        {
            if (_canvas is not null)
            {
                // Canvas mode holds at most one zone.
                if (zone >= 0 && zone < _canvas.ZoneCount)
                {
                    _selection.Add(zone);
                    break;
                }
            }
            else if (_layout is null || (zone >= 0 && zone < _layout.ZoneCount))
            {
                _selection.Add(zone);
            }
        }

        InvalidateVisual();
    }

    /// <summary>Re-evaluates the hover state, for example after the Alt key changed.</summary>
    public void RefreshHover()
    {
        UpdateHover();
        if (_dragging) UpdateDrag(_mouse);
        if (_canvasDrag != CanvasDragKind.None) UpdateCanvasDrag(_mouse);
        InvalidateVisual();
    }

    /// <summary>The zone covering a point given in percent units, or -1 outside the work area.</summary>
    public static int ZoneAt(GridLayout layout, double xPercent, double yPercent)
    {
        if (xPercent < 0 || yPercent < 0 || xPercent > GridLayout.PercentTotal || yPercent > GridLayout.PercentTotal) return -1;
        var cols = Prefix(layout.ColumnPercents);
        var rows = Prefix(layout.RowPercents);
        int c = IndexOf(cols, xPercent);
        int r = IndexOf(rows, yPercent);
        return layout.CellMap[r][c];
    }

    // ---- geometry helpers ----

    private GridLayout? DisplayLayout => _dragLayout ?? _layout;

    private Rect AreaRect()
    {
        double availW = Math.Max(1, ActualWidth - 2 * OuterMargin);
        double availH = Math.Max(1, ActualHeight - 2 * OuterMargin);
        double aspect = _workWidth / (double)_workHeight;
        double w = availW;
        double h = w / aspect;
        if (h > availH)
        {
            h = availH;
            w = h * aspect;
        }

        // Left aligned, so widening the window never moves the screen sideways.
        return new Rect(OuterMargin, (ActualHeight - h) / 2, w, h);
    }

    private static int[] Prefix(IReadOnlyList<int> percents)
    {
        var edges = new int[percents.Count + 1];
        for (int i = 0; i < percents.Count; i++) edges[i + 1] = edges[i] + percents[i];
        return edges;
    }

    private static int IndexOf(int[] edges, double value)
    {
        for (int i = 0; i < edges.Length - 1; i++)
        {
            if (value < edges[i + 1]) return i;
        }

        return edges.Length - 2;
    }

    private static Rect ToRect(Rect area, int left, int top, int right, int bottom) => new(
        area.X + area.Width * left / 10000.0,
        area.Y + area.Height * top / 10000.0,
        area.Width * (right - left) / 10000.0,
        area.Height * (bottom - top) / 10000.0);

    private static double PercentX(Rect area, Point p) => (p.X - area.X) / area.Width * 10000.0;

    private static double PercentY(Rect area, Point p) => (p.Y - area.Y) / area.Height * 10000.0;

    private int PixelsX(int percent) => (int)Math.Round(percent * (double)_workWidth / 10000.0);

    private int PixelsY(int percent) => (int)Math.Round(percent * (double)_workHeight / 10000.0);

    // ---- boundary segments ----

    /// <summary>A visible stretch of an interior line: the cells on its two sides belong to different zones.
    /// Positions are in percent units; the line index matches ZoneGridEditor.InteriorLines.</summary>
    private readonly record struct Segment(ZoneAxis Axis, int LineIndex, int Position, int From, int To);

    private static List<Segment> BuildSegments(GridLayout g)
    {
        var result = new List<Segment>();
        var cols = Prefix(g.ColumnPercents);
        var rows = Prefix(g.RowPercents);
        for (int i = 0; i < cols.Length - 2; i++)
        {
            int? start = null;
            for (int r = 0; r < rows.Length - 1; r++)
            {
                bool edge = g.CellMap[r][i] != g.CellMap[r][i + 1];
                if (edge && start is null) start = rows[r];
                if (!edge && start is not null)
                {
                    result.Add(new Segment(ZoneAxis.Vertical, i, cols[i + 1], start.Value, rows[r]));
                    start = null;
                }
            }

            if (start is not null) result.Add(new Segment(ZoneAxis.Vertical, i, cols[i + 1], start.Value, rows[^1]));
        }

        for (int i = 0; i < rows.Length - 2; i++)
        {
            int? start = null;
            for (int c = 0; c < cols.Length - 1; c++)
            {
                bool edge = g.CellMap[i][c] != g.CellMap[i + 1][c];
                if (edge && start is null) start = cols[c];
                if (!edge && start is not null)
                {
                    result.Add(new Segment(ZoneAxis.Horizontal, i, rows[i + 1], start.Value, cols[c]));
                    start = null;
                }
            }

            if (start is not null) result.Add(new Segment(ZoneAxis.Horizontal, i, rows[i + 1], start.Value, cols[^1]));
        }

        return result;
    }

    private static (Point A, Point B) SegmentPoints(Rect area, Segment s)
    {
        if (s.Axis == ZoneAxis.Vertical)
        {
            double x = area.X + area.Width * s.Position / 10000.0;
            return (new Point(x, area.Y + area.Height * s.From / 10000.0), new Point(x, area.Y + area.Height * s.To / 10000.0));
        }

        double y = area.Y + area.Height * s.Position / 10000.0;
        return (new Point(area.X + area.Width * s.From / 10000.0, y), new Point(area.X + area.Width * s.To / 10000.0, y));
    }

    private static Segment? HitSegment(GridLayout g, Rect area, Point p)
    {
        Segment? best = null;
        double bestDistance = double.MaxValue;
        foreach (var s in BuildSegments(g))
        {
            var (a, b) = SegmentPoints(area, s);
            double distance;
            if (s.Axis == ZoneAxis.Vertical)
            {
                if (p.Y < a.Y - LineHitTolerance || p.Y > b.Y + LineHitTolerance) continue;
                distance = Math.Abs(p.X - a.X);
            }
            else
            {
                if (p.X < a.X - LineHitTolerance || p.X > b.X + LineHitTolerance) continue;
                distance = Math.Abs(p.Y - a.Y);
            }

            if (distance <= LineHitTolerance && distance < bestDistance)
            {
                best = s;
                bestDistance = distance;
            }
        }

        return best;
    }

    // ---- hover, drag, split ----

    private void UpdateHover()
    {
        if (_canvas is not null)
        {
            UpdateCanvasHover();
            return;
        }

        _hoverSegment = null;
        _splitZone = -1;
        _splitPreview = null;
        var g = DisplayLayout;
        if (g is null || !_mouseInside || _dragging || ActualWidth < 40)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        var area = AreaRect();
        if (_tool == ZoneEditTool.Select)
        {
            _hoverSegment = HitSegment(g, area, _mouse);
            Cursor = _hoverSegment is { } s
                ? (s.Axis == ZoneAxis.Vertical ? Cursors.SizeWE : Cursors.SizeNS)
                : Cursors.Arrow;
            return;
        }

        int zone = ZoneAt(g, PercentX(area, _mouse), PercentY(area, _mouse));
        if (zone < 0)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        Cursor = Cursors.Cross;
        // Alt flips the cut to the other axis while it is held; Shift puts the cut at the centre of the zone.
        var axis = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            ? (_splitAxis == ZoneAxis.Vertical ? ZoneAxis.Horizontal : ZoneAxis.Vertical)
            : _splitAxis;
        int position;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var bounds = ZoneGridEditor.ZoneBounds(g, zone);
            position = axis == ZoneAxis.Vertical ? (bounds.Left + bounds.Right) / 2 : (bounds.Top + bounds.Bottom) / 2;
        }
        else
        {
            double raw = axis == ZoneAxis.Vertical ? PercentX(area, _mouse) : PercentY(area, _mouse);
            position = RoundPercent(raw);
        }

        _splitZone = zone;
        _splitPosition = position;
        _splitPreviewAxis = axis;
        _splitPreview = ZoneGridEditor.Split(g, zone, axis, position);
    }

    private void UpdateDrag(Point p)
    {
        if (_dragSegment is not { } s || _layout is null) return;
        var area = AreaRect();
        double raw = s.Axis == ZoneAxis.Vertical ? PercentX(area, p) : PercentY(area, p);
        int position = RoundPercent(raw);
        // Shift rounds the line to a whole 1% (100 units) so the percentages stay clean
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) position = (int)Math.Round(position / 100.0) * 100;
        _dragLayout = ZoneGridEditor.MoveLine(_layout, s.Axis, s.LineIndex, position).Layout;
    }

    private void EndDrag(bool commit)
    {
        var result = _dragLayout;
        _dragging = false;
        _dragSegment = null;
        _dragLayout = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateHover();
        InvalidateVisual();
        if (commit && result is not null && _layout is not null && !ZoneGridEditor.SameLayout(result, _layout))
        {
            EditCommitted?.Invoke(this, result);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouseInside = true;
        _mouse = e.GetPosition(this);
        if (_dragging) UpdateDrag(_mouse);
        else if (_canvasDrag != CanvasDragKind.None) UpdateCanvasDrag(_mouse);
        else UpdateHover();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging || _canvasDrag != CanvasDragKind.None) return;
        _mouseInside = false;
        UpdateHover();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_canvas is not null)
        {
            CanvasMouseDown(e);
            return;
        }

        var g = DisplayLayout;
        if (g is null || _dragging) return;
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        var area = AreaRect();
        // A number box that kept the focus would swallow Ctrl+Z and the tool letters, so the preview takes it like in canvas mode.
        Focus();

        if (_tool == ZoneEditTool.Split)
        {
            UpdateHover();
            if (_splitPreview is { } preview)
            {
                if (preview.Changed)
                {
                    Notice?.Invoke(this, null);
                    EditCommitted?.Invoke(this, preview.Layout);
                }
                else
                {
                    Notice?.Invoke(this, preview.Reason);
                }
            }

            e.Handled = true;
            return;
        }

        if (HitSegment(g, area, _mouse) is { } segment)
        {
            _dragSegment = segment;
            _dragging = true;
            _dragLayout = _layout;
            CaptureMouse();
            Notice?.Invoke(this, null);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        int zone = ZoneAt(g, PercentX(area, _mouse), PercentY(area, _mouse));
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool changed;
        if (zone < 0)
        {
            changed = !control && _selection.Count > 0;
            if (!control) _selection.Clear();
        }
        else if (control)
        {
            changed = true;
            if (!_selection.Remove(zone)) _selection.Add(zone);
        }
        else
        {
            changed = _selection.Count != 1 || !_selection.Contains(zone);
            _selection.Clear();
            _selection.Add(zone);
        }

        InvalidateVisual();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_canvasDrag != CanvasDragKind.None)
        {
            _mouse = e.GetPosition(this);
            UpdateCanvasDrag(_mouse);
            EndCanvasDrag();
            e.Handled = true;
            return;
        }

        if (_dragging)
        {
            _mouse = e.GetPosition(this);
            UpdateDrag(_mouse);
            EndDrag(commit: true);
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) EndDrag(commit: false);
        CancelCanvasDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _canvasDrag != CanvasDragKind.None)
        {
            CancelCanvasDrag();
            e.Handled = true;
        }
    }

    // ---- canvas mode: hit testing, gestures ----

    private int SelectedCanvasZone()
    {
        if (_canvas is null || _selection.Count != 1) return -1;
        int zone = _selection.First();
        return zone >= 0 && zone < _canvas.ZoneCount ? zone : -1;
    }

    /// <summary>The top-most zone (highest index) containing a point in percent units, or -1.</summary>
    private static int CanvasZoneAt(CanvasLayout layout, double xPercent, double yPercent)
    {
        for (int i = layout.ZoneCount - 1; i >= 0; i--)
        {
            var z = layout.Zones[i];
            if (xPercent >= z.Left && xPercent <= z.Right && yPercent >= z.Top && yPercent <= z.Bottom) return i;
        }

        return -1;
    }

    /// <summary>The edges of a zone rectangle the point grabs: within <see cref="HandleTolerance"/> of an edge,
    /// outside or inside the rectangle (inside, a small zone keeps a middle third as body). None for the body.</summary>
    private static CanvasHandle HitHandle(Rect rect, Point p)
    {
        int horizontal = NearEdge(p.X, rect.Left, rect.Right);
        int vertical = NearEdge(p.Y, rect.Top, rect.Bottom);
        // The point must be within reach of the rectangle on both axes, or an edge far away on the other axis would count.
        if (p.X < rect.Left - HandleTolerance || p.X > rect.Right + HandleTolerance) return CanvasHandle.None;
        if (p.Y < rect.Top - HandleTolerance || p.Y > rect.Bottom + HandleTolerance) return CanvasHandle.None;
        var handle = CanvasHandle.None;
        if (horizontal < 0) handle |= CanvasHandle.Left;
        else if (horizontal > 0) handle |= CanvasHandle.Right;
        if (vertical < 0) handle |= CanvasHandle.Top;
        else if (vertical > 0) handle |= CanvasHandle.Bottom;
        return handle;
    }

    // -1: the low edge, 1: the high edge, 0: neither.
    private static int NearEdge(double v, double low, double high)
    {
        if (v < low) return low - v <= HandleTolerance ? -1 : 0;
        if (v > high) return v - high <= HandleTolerance ? 1 : 0;
        double inner = Math.Min(HandleTolerance, (high - low) / 3);
        double toLow = v - low;
        double toHigh = high - v;
        if (toLow <= inner && toLow <= toHigh) return -1;
        return toHigh <= inner ? 1 : 0;
    }

    private static Cursor CursorFor(CanvasHandle handle)
    {
        bool horizontal = handle.HasFlag(CanvasHandle.Left) || handle.HasFlag(CanvasHandle.Right);
        bool vertical = handle.HasFlag(CanvasHandle.Top) || handle.HasFlag(CanvasHandle.Bottom);
        if (horizontal && vertical)
        {
            bool diagonal = handle.HasFlag(CanvasHandle.Left) == handle.HasFlag(CanvasHandle.Top);
            return diagonal ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }

        return horizontal ? Cursors.SizeWE : vertical ? Cursors.SizeNS : Cursors.SizeAll;
    }

    private CanvasHandle SelectedHandleAt(Rect area, Point p)
    {
        int selected = SelectedCanvasZone();
        if (selected < 0 || _canvas is null) return CanvasHandle.None;
        var z = _canvas.Zones[selected];
        return HitHandle(ToRect(area, z.Left, z.Top, z.Right, z.Bottom), p);
    }

    private void UpdateCanvasHover()
    {
        _canvasSplitZone = -1;
        _canvasSplitPreview = null;
        // During a drag the cursor chosen at its start stays.
        if (_canvasDrag != CanvasDragKind.None) return;
        if (_canvas is null || !_mouseInside || ActualWidth < 40)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        switch (_tool)
        {
            case ZoneEditTool.Draw:
                Cursor = Cursors.Cross;
                break;
            case ZoneEditTool.Split:
                UpdateCanvasSplitPreview();
                break;
            case ZoneEditTool.Select:
                var area = AreaRect();
                var handle = SelectedHandleAt(area, _mouse);
                if (handle != CanvasHandle.None) Cursor = CursorFor(handle);
                else if (CanvasZoneAt(_canvas, PercentX(area, _mouse), PercentY(area, _mouse)) >= 0) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Arrow;
                break;
            default:
                Cursor = Cursors.Arrow;
                break;
        }
    }

    /// <summary>Split tool on free-form zones: the cut goes through the top-most zone under the pointer. Alt flips the
    /// direction and Shift cuts at the centre of the zone, as for a grid.</summary>
    private void UpdateCanvasSplitPreview()
    {
        var canvas = _canvas;
        if (canvas is null) return;
        var area = AreaRect();
        double x = PercentX(area, _mouse);
        double y = PercentY(area, _mouse);
        int zone = CanvasZoneAt(canvas, x, y);
        if (zone < 0)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        Cursor = Cursors.Cross;
        var axis = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            ? (_splitAxis == ZoneAxis.Vertical ? ZoneAxis.Horizontal : ZoneAxis.Vertical)
            : _splitAxis;
        var z = canvas.Zones[zone];
        int position;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            position = axis == ZoneAxis.Vertical ? (z.Left + z.Right) / 2 : (z.Top + z.Bottom) / 2;
        else
            position = RoundPercent(axis == ZoneAxis.Vertical ? x : y);

        _canvasSplitZone = zone;
        _splitPosition = position;
        _splitPreviewAxis = axis;
        _canvasSplitPreview = ZoneCanvasEditor.SplitZone(canvas, zone, axis, position);
    }

    /// <summary>A raw position in percent units, rounded and kept inside the work area.</summary>
    private static int RoundPercent(double raw) => (int)Math.Round(Math.Clamp(raw, 0, 10000));

    private void CanvasMouseDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_canvas is null || _canvasDrag != CanvasDragKind.None) return;
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        var area = AreaRect();
        Focus();

        if (_tool == ZoneEditTool.Draw)
        {
            _canvasAnchorX = RoundPercent(PercentX(area, _mouse));
            _canvasAnchorY = RoundPercent(PercentY(area, _mouse));
            BeginCanvasDrag(CanvasDragKind.Create, CanvasHandle.None, -1, Cursors.Cross);
            return;
        }

        if (_tool == ZoneEditTool.Split)
        {
            UpdateCanvasHover();
            if (_canvasSplitPreview is { } split)
            {
                if (split.Changed)
                {
                    Notice?.Invoke(this, null);
                    CanvasEditCommitted?.Invoke(this, split.Layout);
                }
                else
                {
                    Notice?.Invoke(this, split.Reason);
                }
            }

            return;
        }

        if (_tool != ZoneEditTool.Select) return;

        int selected = SelectedCanvasZone();
        var handle = SelectedHandleAt(area, _mouse);
        if (handle != CanvasHandle.None)
        {
            BeginCanvasDrag(CanvasDragKind.Resize, handle, selected, CursorFor(handle));
            return;
        }

        int zone = CanvasZoneAt(_canvas, PercentX(area, _mouse), PercentY(area, _mouse));
        if (zone >= 0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+click adds a zone to the selection or takes it out (the zones to merge); no move starts.
            if (!_selection.Remove(zone)) _selection.Add(zone);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (zone != selected)
        {
            _selection.Clear();
            if (zone >= 0) _selection.Add(zone);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // Pressing a body also arms a move: it only happens once the mouse travelled, so a click just selects.
        if (zone >= 0) BeginCanvasDrag(CanvasDragKind.Move, CanvasHandle.None, zone, Cursors.SizeAll);
        InvalidateVisual();
    }

    private void BeginCanvasDrag(CanvasDragKind kind, CanvasHandle handle, int zone, Cursor cursor)
    {
        if (_canvas is null) return;
        var area = AreaRect();
        _canvasDrag = kind;
        _canvasHandle = handle;
        _canvasZone = zone;
        _canvasStartZone = zone >= 0 ? _canvas.Zones[zone] : null;
        _canvasStartMouse = _mouse;
        _canvasStartPercent = new Point(PercentX(area, _mouse), PercentY(area, _mouse));
        _canvasDragMoved = false;
        _canvasPreview = null;
        _canvasBand = null;
        _canvasResult = null;
        Cursor = cursor;
        CaptureMouse();
        Notice?.Invoke(this, null);
        InvalidateVisual();
    }

    private void UpdateCanvasDrag(Point p)
    {
        if (_canvas is null || _canvasDrag == CanvasDragKind.None) return;
        if (!_canvasDragMoved)
        {
            if ((p - _canvasStartMouse).Length < DragThreshold) return;
            _canvasDragMoved = true;
        }

        var area = AreaRect();
        double x = PercentX(area, p);
        double y = PercentY(area, p);
        switch (_canvasDrag)
        {
            case CanvasDragKind.Create:
            {
                int cx = RoundPercent(x);
                int cy = RoundPercent(y);
                var band = new CanvasZone(
                    Math.Min(_canvasAnchorX, cx), Math.Min(_canvasAnchorY, cy),
                    Math.Max(_canvasAnchorX, cx), Math.Max(_canvasAnchorY, cy));
                _canvasBand = band;
                _canvasResult = ZoneCanvasEditor.AddZone(_canvas, band.Left, band.Top, band.Right, band.Bottom);
                _canvasPreview = null;
                break;
            }

            case CanvasDragKind.Resize when _canvasStartZone is { } start:
            {
                double dx = x - _canvasStartPercent.X;
                double dy = y - _canvasStartPercent.Y;
                int? left = null, top = null, right = null, bottom = null;
                if (_canvasHandle.HasFlag(CanvasHandle.Left)) left = RoundPercent(start.Left + dx);
                if (_canvasHandle.HasFlag(CanvasHandle.Right)) right = RoundPercent(start.Right + dx);
                if (_canvasHandle.HasFlag(CanvasHandle.Top)) top = RoundPercent(start.Top + dy);
                if (_canvasHandle.HasFlag(CanvasHandle.Bottom)) bottom = RoundPercent(start.Bottom + dy);
                int l = left ?? start.Left, t = top ?? start.Top, r = right ?? start.Right, b = bottom ?? start.Bottom;
                _canvasBand = new CanvasZone(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
                var result = ZoneCanvasEditor.SetZoneEdges(_canvas, _canvasZone, left, top, right, bottom);
                _canvasResult = result;
                _canvasPreview = result.Changed ? result.Layout : null;
                break;
            }

            case CanvasDragKind.Move when _canvasStartZone is { } start:
            {
                int dx = (int)Math.Round(x - _canvasStartPercent.X);
                int dy = (int)Math.Round(y - _canvasStartPercent.Y);
                var result = ZoneCanvasEditor.MoveZone(_canvas, _canvasZone, dx, dy);
                _canvasResult = result;
                _canvasPreview = result.Changed ? result.Layout : null;
                break;
            }
        }

        InvalidateVisual();
    }

    private void ResetCanvasDrag()
    {
        _canvasDrag = CanvasDragKind.None;
        _canvasHandle = CanvasHandle.None;
        _canvasZone = -1;
        _canvasStartZone = null;
        _canvasDragMoved = false;
        _canvasPreview = null;
        _canvasBand = null;
        _canvasResult = null;
    }

    /// <summary>Drops a canvas drag in progress without committing it (Escape, a new layout, lost capture).</summary>
    private void CancelCanvasDrag()
    {
        if (_canvasDrag == CanvasDragKind.None) return;
        ResetCanvasDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateHover();
        InvalidateVisual();
    }

    /// <summary>Finishes a canvas drag: raises <see cref="CanvasEditCommitted"/> once when the layout changed, or
    /// <see cref="Notice"/> with the reason when the editor refused.</summary>
    private void EndCanvasDrag()
    {
        var kind = _canvasDrag;
        var result = _canvasResult;
        var layout = _canvas;
        ResetCanvasDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateHover();
        InvalidateVisual();
        if (result is null || layout is null) return;

        if (result.Changed && !ZoneCanvasEditor.SameLayout(result.Layout, layout))
        {
            Notice?.Invoke(this, null);
            CanvasEditCommitted?.Invoke(this, result.Layout);
            // The window pushed the new layout back by now: a drawn zone ends up selected.
            int added = result.Layout.ZoneCount - 1;
            if (kind == CanvasDragKind.Create && _canvas is not null && added < _canvas.ZoneCount)
            {
                _selection.Clear();
                _selection.Add(added);
                InvalidateVisual();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (result.Reason is { } reason)
        {
            Notice?.Invoke(this, reason);
        }
    }

    /// <summary>Leaves canvas mode (grid layout set, or a null canvas layout): drops any drag and the selection.</summary>
    private void LeaveCanvasMode()
    {
        if (_canvas is null) return;
        CancelCanvasDrag();
        _canvas = null;
        if (_selection.Count > 0)
        {
            _selection.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- drawing ----

    private void RebuildBrushes()
    {
        var a = _appearance;
        // Darker than the window background, so the monitor area stands out as the single container.
        _areaBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
        // The saved opacities are tuned for an overlay on a desktop; floors keep the preview readable.
        _fillBrush = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(a.Fill, Math.Max(a.FillOpacityPercent, 28))));
        _highlightBrush = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(a.HighlightFill, Math.Max(a.HighlightOpacityPercent, 55))));
        // Canvas zones overlap, so the fill stays partly transparent whatever the saved opacity is.
        _canvasFillBrush = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(a.Fill, CanvasFillOpacityPercent)));
        var border = Freeze(new SolidColorBrush(a.Border));
        _zonePen = new Pen(border, Math.Clamp(a.BorderThickness, 1, 3));
        _zonePen.Freeze();
        _selectedPen = new Pen(Freeze(new SolidColorBrush(Colors.White)), 2.5);
        _selectedPen.Freeze();
        _guidePen = new Pen(Freeze(new SolidColorBrush(Colors.White)), 2) { DashStyle = DashStyles.Dash };
        _guidePen.Freeze();
        _refusedGuidePen = new Pen(Freeze(new SolidColorBrush(Colors.OrangeRed)), 2) { DashStyle = DashStyles.Dash };
        _refusedGuidePen.Freeze();
        _linePen = new Pen(Freeze(new SolidColorBrush(Colors.White)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _linePen.Freeze();
    }

    private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // A transparent backdrop so clicks on the empty background reach the mouse handlers.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_canvas is not null)
        {
            RenderCanvas(drawingContext);
            return;
        }

        var g = DisplayLayout;
        if (g is null || ActualWidth < 40 || ActualHeight < 40) return;

        var area = AreaRect();
        drawingContext.DrawRoundedRectangle(_areaBrush, null, area, 4, 4);

        double scale = area.Width / _workWidth;
        double spacing = g.Spacing * scale;
        for (int zone = 0; zone < g.ZoneCount; zone++)
        {
            var (l, t, r, b) = ZoneGridEditor.ZoneBounds(g, zone);
            var rect = ToRect(area, l, t, r, b);
            rect = new Rect(
                rect.X + (l == 0 ? spacing : spacing / 2),
                rect.Y + (t == 0 ? spacing : spacing / 2),
                Math.Max(2, rect.Width - (l == 0 ? spacing : spacing / 2) - (r == 10000 ? spacing : spacing / 2)),
                Math.Max(2, rect.Height - (t == 0 ? spacing : spacing / 2) - (b == 10000 ? spacing : spacing / 2)));
            bool selected = _selection.Contains(zone);
            double radius = Math.Min(_appearance.CornerRadius, Math.Min(rect.Width, rect.Height) / 4);
            drawingContext.DrawRoundedRectangle(selected ? _highlightBrush : _fillBrush, selected ? _selectedPen : _zonePen, rect, radius, radius);
            DrawZoneLabel(drawingContext, rect, zone, r - l, b - t);
        }

        // The hovered or dragged boundary line, on top of the zones.
        if (_dragging && _dragSegment is { } dragged)
        {
            foreach (var s in BuildSegments(g).Where(s => s.Axis == dragged.Axis && s.LineIndex == dragged.LineIndex))
            {
                var (a, b) = SegmentPoints(area, s);
                drawingContext.DrawLine(_linePen, a, b);
            }

            var lines = ZoneGridEditor.InteriorLines(g, dragged.Axis);
            if (dragged.LineIndex < lines.Count)
            {
                int position = lines[dragged.LineIndex];
                int pixels = dragged.Axis == ZoneAxis.Vertical ? PixelsX(position) : PixelsY(position);
                DrawLabel(drawingContext, $"{position / 100.0:0.##} %  ({pixels} px)", _mouse);
            }
        }
        else if (_hoverSegment is { } hover)
        {
            var (a, b) = SegmentPoints(area, hover);
            drawingContext.DrawLine(_linePen, a, b);
        }

        if (_tool == ZoneEditTool.Split && _splitZone >= 0 && _splitPreview is { } preview)
        {
            DrawSplitGuide(drawingContext, area, g, preview);
        }
    }

    // The zone number centred in a rectangle and, when it is big enough, the pixel size below it.
    private void DrawZoneLabel(DrawingContext dc, Rect rect, int zone, int widthPercent, int heightPercent)
    {
        double fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) / 2.5, 11, 30);
        var number = MakeText((zone + 1).ToString(CultureInfo.CurrentCulture), fontSize, Brushes.White);
        double blockHeight = number.Height;
        string size = $"{PixelsX(widthPercent)} x {PixelsY(heightPercent)}";
        bool showSize = rect.Height > 64 && rect.Width > 80;
        var sizeText = showSize ? MakeText(size, 11, Brushes.Gainsboro) : null;
        if (sizeText is not null) blockHeight += sizeText.Height;
        double top = rect.Y + (rect.Height - blockHeight) / 2;
        dc.DrawText(number, new Point(rect.X + (rect.Width - number.Width) / 2, top));
        if (sizeText is not null)
        {
            dc.DrawText(sizeText, new Point(rect.X + (rect.Width - sizeText.Width) / 2, top + number.Height));
        }
    }

    private void RenderCanvas(DrawingContext dc)
    {
        var canvas = _canvasPreview ?? _canvas;
        if (canvas is null || ActualWidth < 40 || ActualHeight < 40) return;

        var area = AreaRect();
        dc.DrawRoundedRectangle(_areaBrush, null, area, 4, 4);

        // Index order, so later zones paint over earlier ones; the selected zone goes last, over its neighbours.
        for (int zone = 0; zone < canvas.ZoneCount; zone++)
        {
            if (!_selection.Contains(zone)) DrawCanvasZone(dc, area, canvas, zone, selected: false);
        }

        foreach (int zone in _selection.Where(i => i >= 0 && i < canvas.ZoneCount).OrderBy(i => i))
            DrawCanvasZone(dc, area, canvas, zone, selected: true);

        if (_tool == ZoneEditTool.Split && _canvasSplitZone >= 0 && _canvasSplitPreview is { } splitPreview)
            DrawCanvasSplitGuide(dc, area, canvas, splitPreview);

        if (_canvasBand is { } band)
        {
            bool refused = _canvasResult is { Changed: false, Reason: not null };
            var rect = ToRect(area, band.Left, band.Top, band.Right, band.Bottom);
            dc.DrawRectangle(null, refused ? _refusedGuidePen : _guidePen, rect);
            DrawLabel(dc, $"{PixelsX(band.Right - band.Left)} x {PixelsY(band.Bottom - band.Top)}", _mouse);
        }
    }

    private void DrawCanvasSplitGuide(DrawingContext dc, Rect area, CanvasLayout canvas, CanvasEditResult preview)
    {
        var z = canvas.Zones[_canvasSplitZone];
        var pen = preview.Changed ? _guidePen : _refusedGuidePen;
        string text;
        if (_splitPreviewAxis == ZoneAxis.Vertical)
        {
            double x = area.X + area.Width * _splitPosition / 10000.0;
            dc.DrawLine(pen, new Point(x, area.Y + area.Height * z.Top / 10000.0), new Point(x, area.Y + area.Height * z.Bottom / 10000.0));
            text = preview.Changed ? $"{PixelsX(_splitPosition - z.Left)} px | {PixelsX(z.Right - _splitPosition)} px" : preview.Reason ?? string.Empty;
        }
        else
        {
            double y = area.Y + area.Height * _splitPosition / 10000.0;
            dc.DrawLine(pen, new Point(area.X + area.Width * z.Left / 10000.0, y), new Point(area.X + area.Width * z.Right / 10000.0, y));
            text = preview.Changed ? $"{PixelsY(_splitPosition - z.Top)} px | {PixelsY(z.Bottom - _splitPosition)} px" : preview.Reason ?? string.Empty;
        }

        if (text.Length > 0) DrawLabel(dc, text, _mouse);
    }

    private void DrawCanvasZone(DrawingContext dc, Rect area, CanvasLayout canvas, int zone, bool selected)
    {
        var z = canvas.Zones[zone];
        var rect = ToRect(area, z.Left, z.Top, z.Right, z.Bottom);
        rect = new Rect(rect.X, rect.Y, Math.Max(2, rect.Width), Math.Max(2, rect.Height));
        double radius = Math.Min(_appearance.CornerRadius, Math.Min(rect.Width, rect.Height) / 4);
        dc.DrawRoundedRectangle(selected ? _highlightBrush : _canvasFillBrush, selected ? _selectedPen : _zonePen, rect, radius, radius);
        DrawZoneLabel(dc, rect, zone, z.Right - z.Left, z.Bottom - z.Top);
    }

    private void DrawSplitGuide(DrawingContext dc, Rect area, GridLayout g, EditResult preview)
    {
        var (l, t, r, b) = ZoneGridEditor.ZoneBounds(g, _splitZone);
        var pen = preview.Changed ? _guidePen : _refusedGuidePen;
        string text;
        if (_splitPreviewAxis == ZoneAxis.Vertical)
        {
            double x = area.X + area.Width * _splitPosition / 10000.0;
            double y0 = area.Y + area.Height * t / 10000.0;
            double y1 = area.Y + area.Height * b / 10000.0;
            dc.DrawLine(pen, new Point(x, y0), new Point(x, y1));
            text = preview.Changed
                ? $"{PixelsX(_splitPosition - l)} px | {PixelsX(r - _splitPosition)} px"
                : preview.Reason ?? string.Empty;
        }
        else
        {
            double y = area.Y + area.Height * _splitPosition / 10000.0;
            double x0 = area.X + area.Width * l / 10000.0;
            double x1 = area.X + area.Width * r / 10000.0;
            dc.DrawLine(pen, new Point(x0, y), new Point(x1, y));
            text = preview.Changed
                ? $"{PixelsY(_splitPosition - t)} px | {PixelsY(b - _splitPosition)} px"
                : preview.Reason ?? string.Empty;
        }

        if (text.Length > 0) DrawLabel(dc, text, _mouse);
    }

    private void DrawLabel(DrawingContext dc, string text, Point anchor)
    {
        var formatted = MakeText(text, 12, Brushes.White);
        double w = formatted.Width + 12;
        double h = formatted.Height + 6;
        double x = Math.Clamp(anchor.X + 14, 0, Math.Max(0, ActualWidth - w));
        double y = Math.Clamp(anchor.Y + 14, 0, Math.Max(0, ActualHeight - h));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x10)), null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(formatted, new Point(x + 6, y + 3));
    }

    private FormattedText MakeText(string text, double size, Brush brush) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        LabelFace,
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
