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
}

/// <summary>The interactive zone preview of the zone editor: draws a layout scaled to the aspect ratio of a
/// monitor work area, selects zones, drags boundary lines and shows the split guide. It never owns the layout:
/// every gesture ends in <see cref="EditCommitted"/> with a layout produced by <see cref="ZoneGridEditor"/>, and
/// the window pushes it into its <see cref="ZoneEditHistory"/> and hands the new state back through
/// <see cref="SetLayout"/>.</summary>
public sealed class ZonePreviewCanvas : FrameworkElement
{
    private const double OuterMargin = 12;
    private const double LineHitTolerance = 5;
    private const int SnapStepPercent = 100;
    private const int SnapTolerancePercent = 150;
    private static readonly int[] ReferencePoints = [2500, 5000, 7500];
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
    private EditResult? _splitPreview;

    private Brush _areaBrush = Brushes.Black;
    private Pen _areaPen = new(Brushes.Gray, 1);
    private Brush _fillBrush = Brushes.Transparent;
    private Brush _highlightBrush = Brushes.Transparent;
    private Pen _zonePen = new(Brushes.Gray, 1);
    private Pen _selectedPen = new(Brushes.Gray, 1);
    private Pen _guidePen = new(Brushes.White, 2);
    private Pen _refusedGuidePen = new(Brushes.OrangeRed, 2);
    private Pen _linePen = new(Brushes.White, 3);

    public ZonePreviewCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RebuildBrushes();
    }

    /// <summary>Raised when a gesture produced a new layout that should be recorded in the history.</summary>
    public event EventHandler<GridLayout>? EditCommitted;

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
        _layout = layout;
        _dragLayout = null;
        if (_selection.RemoveWhere(i => i >= layout.ZoneCount) > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateHover();
        InvalidateVisual();
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
            if (_layout is null || (zone >= 0 && zone < _layout.ZoneCount)) _selection.Add(zone);
        }

        InvalidateVisual();
    }

    /// <summary>Re-evaluates the hover state, for example after the Alt key changed.</summary>
    public void RefreshHover()
    {
        UpdateHover();
        if (_dragging) UpdateDrag(_mouse);
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

        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
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

    private static int Snap(int value, GridLayout g, ZoneAxis axis, int skipLine)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return value;
        var candidates = new List<int>(ReferencePoints);
        var lines = ZoneGridEditor.InteriorLines(g, axis);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i != skipLine) candidates.Add(lines[i]);
        }

        return ZoneGridEditor.SnapPosition(value, candidates, SnapStepPercent, SnapTolerancePercent);
    }

    // ---- hover, drag, split ----

    private void UpdateHover()
    {
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
        double raw = _splitAxis == ZoneAxis.Vertical ? PercentX(area, _mouse) : PercentY(area, _mouse);
        int position = Snap((int)Math.Round(Math.Clamp(raw, 0, 10000)), g, _splitAxis, -1);
        _splitZone = zone;
        _splitPosition = position;
        _splitPreview = ZoneGridEditor.Split(g, zone, _splitAxis, position);
    }

    private void UpdateDrag(Point p)
    {
        if (_dragSegment is not { } s || _layout is null) return;
        var area = AreaRect();
        double raw = s.Axis == ZoneAxis.Vertical ? PercentX(area, p) : PercentY(area, p);
        int position = Snap((int)Math.Round(Math.Clamp(raw, 0, 10000)), _layout, s.Axis, s.LineIndex);
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
        else UpdateHover();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        _mouseInside = false;
        UpdateHover();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var g = DisplayLayout;
        if (g is null || _dragging) return;
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        var area = AreaRect();

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
    }

    // ---- drawing ----

    private void RebuildBrushes()
    {
        var a = _appearance;
        _areaBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)));
        _areaPen = new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))), 1);
        _areaPen.Freeze();
        // The saved opacities are tuned for an overlay on a desktop; floors keep the preview readable.
        _fillBrush = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(a.Fill, Math.Max(a.FillOpacityPercent, 28))));
        _highlightBrush = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(a.HighlightFill, Math.Max(a.HighlightOpacityPercent, 55))));
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
        var g = DisplayLayout;
        if (g is null || ActualWidth < 40 || ActualHeight < 40) return;

        var area = AreaRect();
        drawingContext.DrawRoundedRectangle(_areaBrush, _areaPen, area, 4, 4);

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

            double fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) / 2.5, 11, 30);
            var number = MakeText((zone + 1).ToString(CultureInfo.CurrentCulture), fontSize, Brushes.White);
            double blockHeight = number.Height;
            string size = $"{PixelsX(r - l)} x {PixelsY(b - t)}";
            bool showSize = rect.Height > 64 && rect.Width > 80;
            var sizeText = showSize ? MakeText(size, 11, Brushes.Gainsboro) : null;
            if (sizeText is not null) blockHeight += sizeText.Height;
            double top = rect.Y + (rect.Height - blockHeight) / 2;
            drawingContext.DrawText(number, new Point(rect.X + (rect.Width - number.Width) / 2, top));
            if (sizeText is not null)
            {
                drawingContext.DrawText(sizeText, new Point(rect.X + (rect.Width - sizeText.Width) / 2, top + number.Height));
            }
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

    private void DrawSplitGuide(DrawingContext dc, Rect area, GridLayout g, EditResult preview)
    {
        var (l, t, r, b) = ZoneGridEditor.ZoneBounds(g, _splitZone);
        var pen = preview.Changed ? _guidePen : _refusedGuidePen;
        string text;
        if (_splitAxis == ZoneAxis.Vertical)
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
