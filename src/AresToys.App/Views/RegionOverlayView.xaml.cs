using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AresToys.App.Services;

namespace AresToys.App.Views;

/// <summary>What the lens and its coordinates label show for one frame. The session builds it,
/// the view only draws it.</summary>
internal readonly record struct RegionLensFrame(
    BitmapSource? Lens,
    string Coords,
    double CrosshairSize,
    MagnifierPlacement.Placement Placement);

/// <summary>The region capture overlay's window on ONE monitor. It paints that monitor's part of
/// the frozen snapshot and draws the part of the session state that falls on its monitor (dim with
/// its holes, region borders and grips, selection and snap rectangles), in its own DIPs. The
/// popups (lens, cursor label, size label, toolbar) are drawn whole by one view only: the session
/// tells every view what to show or hide. Mouse, wheel and keys are forwarded to the session in
/// physical screen pixels. It holds no selection state and never places itself:
/// <see cref="PerMonitorOverlayHost"/> does.</summary>
internal sealed partial class RegionOverlayView : Window
{
    private const double MagnifierBoxPx = 160;       // lens diameter in DIPs
    private const double GripSizeDips = 10;
    private const double LabelMargin = 6;
    private const double CursorLabelMargin = 16;

    private static readonly Brush SelectedBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 196, 0)));

    private readonly RegionOverlay _session;
    private readonly List<Rectangle> _borders = new();
    private readonly Rectangle[] _grips = new Rectangle[8];
    private Rect[] _dimHolesPx = Array.Empty<Rect>();

    public RegionOverlayView(RegionOverlay session, OverlayMonitor monitor, BitmapSource background, bool toolbarEnabled)
    {
        _session = session;
        Monitor = monitor;
        InitializeComponent();
        // The background is set before the window is ever shown: a first frame without it would
        // flash an empty window over the desktop.
        ScreenshotImage.Source = background;
        // The toolbar is meaningless when the overlay closes on the first selection. Collapsed,
        // not Hidden: the session never shows it then.
        if (!toolbarEnabled) Toolbar.Visibility = Visibility.Collapsed;
        CreateGrips();

        // Toolbar drag: handled at Preview level so the click is consumed before the window-level
        // handler decides to start a region drag. Buttons inside the toolbar opt out via the
        // OriginalSource walk-up.
        Toolbar.PreviewMouseLeftButtonDown += OnToolbarPreviewMouseDown;
        // The dim needs the real size and the screen origin: first build once shown, then on
        // every size change.
        Loaded += (_, _) => RebuildDim();
        SizeChanged += (_, _) => { if (IsLoaded) RebuildDim(); };
    }

    public OverlayMonitor Monitor { get; }

    /// <summary>Physical pixels per DIP on this view's monitor. Only valid once shown.</summary>
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>A physical screen pixel in this view's own DIP space (origin at its top-left).
    /// Only valid once the view is shown.</summary>
    public Point ToDips(double screenX, double screenY) => PointFromScreen(new Point(screenX, screenY));

    private Rect ToDipRect(Rect px)
    {
        var topLeft = ToDips(px.X, px.Y);
        var bottomRight = ToDips(px.Right, px.Bottom);
        return new Rect(topLeft, bottomRight);
    }

    // ---- Dim, regions, selection, snap ------------------------------------------------------

    /// <summary>Rebuild the dim mask with one hole per rectangle (physical screen pixels). Even-odd
    /// fill: every rect cuts a hole through the outer dim; overlapping rects don't double-darken.
    /// The array is kept: the session passes a fresh one per call and never mutates it.</summary>
    public void SetDim(Rect[] holesPx)
    {
        _dimHolesPx = holesPx;
        if (IsLoaded) RebuildDim();
    }

    private void RebuildDim()
    {
        var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
        combined.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var hole in _dimHolesPx) combined.Children.Add(new RectangleGeometry(ToDipRect(hole)));
        combined.Freeze();
        DimPath.Data = combined;
    }

    /// <summary>Draw the committed regions (physical screen pixels) as white dashed borders, the
    /// selected one amber with its eight grips. Cheap: reuses the border rectangles.</summary>
    public void SetRegions(IReadOnlyList<Rect> regionsPx, int selected)
    {
        while (_borders.Count < regionsPx.Count)
        {
            var border = new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            OverlayCanvas.Children.Add(border);
            _borders.Add(border);
        }
        while (_borders.Count > regionsPx.Count)
        {
            OverlayCanvas.Children.Remove(_borders[^1]);
            _borders.RemoveAt(_borders.Count - 1);
        }

        for (var i = 0; i < regionsPx.Count; i++)
        {
            var r = ToDipRect(regionsPx[i]);
            var border = _borders[i];
            var sel = i == selected;
            Canvas.SetLeft(border, r.X);
            Canvas.SetTop(border, r.Y);
            border.Width = r.Width;
            border.Height = r.Height;
            border.Stroke = sel ? SelectedBorderBrush : Brushes.White;
            border.StrokeThickness = sel ? 2.0 : 1.5;
        }

        if (selected < 0 || selected >= regionsPx.Count)
        {
            foreach (var grip in _grips) grip.Visibility = Visibility.Collapsed;
            return;
        }
        var points = RegionOverlay.GripPoints(regionsPx[selected]);
        for (var i = 0; i < 8; i++)
        {
            var c = ToDips(points[i].X, points[i].Y);
            Canvas.SetLeft(_grips[i], c.X - GripSizeDips / 2);
            Canvas.SetTop(_grips[i], c.Y - GripSizeDips / 2);
            _grips[i].Visibility = Visibility.Visible;
        }
    }

    private void CreateGrips()
    {
        for (var i = 0; i < 8; i++)
        {
            var grip = new Rectangle
            {
                Width = GripSizeDips,
                Height = GripSizeDips,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                Cursor = CursorForMode(RegionOverlay.GripModes[i]),
            };
            Canvas.SetZIndex(grip, 100); // above the dim path / rect borders
            _grips[i] = grip;
            OverlayCanvas.Children.Add(grip);
        }
    }

    private static Cursor CursorForMode(RegionDragMode m) => m switch
    {
        RegionDragMode.NW or RegionDragMode.SE => Cursors.SizeNWSE,
        RegionDragMode.NE or RegionDragMode.SW => Cursors.SizeNESW,
        RegionDragMode.N or RegionDragMode.S => Cursors.SizeNS,
        RegionDragMode.E or RegionDragMode.W => Cursors.SizeWE,
        _ => Cursors.Arrow,
    };

    /// <summary>The selection being dragged, or null to hide it.</summary>
    public void SetSelectionRect(Rect? px) => PlaceRectangle(SelectionRect, px);

    /// <summary>The snap preview of the window under the cursor, or null to hide it.</summary>
    public void SetSnapRect(Rect? px) => PlaceRectangle(SnapRect, px);

    private void PlaceRectangle(Rectangle shape, Rect? px)
    {
        if (px is not { } r)
        {
            shape.Visibility = Visibility.Collapsed;
            return;
        }
        var d = ToDipRect(r);
        Canvas.SetLeft(shape, d.X);
        Canvas.SetTop(shape, d.Y);
        shape.Width = d.Width;
        shape.Height = d.Height;
        shape.Visibility = Visibility.Visible;
    }

    // ---- Popups: each one is drawn whole by one view ---------------------------------------

    /// <summary>The "X: Y: W: H:" label at <paramref name="anchorPx"/> + 6 DIPs, kept whole inside
    /// this monitor; null hides it. It is shared by the drag and the snap preview.</summary>
    public void SetSizeLabel(string? text, Point anchorPx)
    {
        if (text is null)
        {
            SizeLabelBorder.Visibility = Visibility.Collapsed;
            return;
        }
        SizeLabel.Text = text;
        var size = DesiredSizeOf(SizeLabelBorder);
        var a = ToDips(anchorPx.X, anchorPx.Y);
        Canvas.SetLeft(SizeLabelBorder, Clamp(a.X + LabelMargin, LabelMargin, ActualWidth - size.Width - LabelMargin));
        Canvas.SetTop(SizeLabelBorder, Clamp(a.Y + LabelMargin, LabelMargin, ActualHeight - size.Height - LabelMargin));
        SizeLabelBorder.Visibility = Visibility.Visible;
    }

    /// <summary>The cursor position label at the bottom-right of this monitor; null hides it.</summary>
    public void SetCursorLabel(string? text)
    {
        if (text is null)
        {
            CursorPosBorder.Visibility = Visibility.Collapsed;
            return;
        }
        CursorPosLabel.Text = text;
        var size = DesiredSizeOf(CursorPosBorder);
        Canvas.SetLeft(CursorPosBorder, ActualWidth - size.Width - CursorLabelMargin);
        Canvas.SetTop(CursorPosBorder, ActualHeight - size.Height - CursorLabelMargin);
        CursorPosBorder.Visibility = Visibility.Visible;
    }

    private static Size DesiredSizeOf(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    // A monitor smaller than the label still keeps its left/top edge.
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    /// <summary>The work area of this view's monitor in this view's DIPs. The work area, not the
    /// full monitor rect, keeps the lens clear of the taskbar.</summary>
    public MagnifierPlacement.Rect WorkAreaInDips()
    {
        var wa = Monitor.WorkArea;
        var topLeft = ToDips(wa.X, wa.Y);
        var bottomRight = ToDips(wa.X + wa.Width, wa.Y + wa.Height);
        return new MagnifierPlacement.Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>Current size of the lens and of its label, with the design sizes as the fallback
    /// before the first layout pass.</summary>
    public (double CircleW, double CircleH, double LabelW, double LabelH) MagnifierSizes() => (
        MagnifierGroup.ActualWidth > 0 ? MagnifierGroup.ActualWidth : MagnifierBoxPx,
        MagnifierGroup.ActualHeight > 0 ? MagnifierGroup.ActualHeight : MagnifierBoxPx,
        MagnifierCoordsBorder.ActualWidth > 0 ? MagnifierCoordsBorder.ActualWidth : 120,
        MagnifierCoordsBorder.ActualHeight > 0 ? MagnifierCoordsBorder.ActualHeight : 28);

    public void ShowMagnifier(in RegionLensFrame frame)
    {
        if (frame.Lens is not null) MagnifierImage.Source = frame.Lens;
        MagnifierCoords.Text = frame.Coords;
        MagnifierCrosshair.Width = frame.CrosshairSize;
        MagnifierCrosshair.Height = frame.CrosshairSize;
        Canvas.SetLeft(MagnifierGroup, frame.Placement.CircleX);
        Canvas.SetTop(MagnifierGroup, frame.Placement.CircleY);
        Canvas.SetLeft(MagnifierCoordsBorder, frame.Placement.LabelsX);
        Canvas.SetTop(MagnifierCoordsBorder, frame.Placement.LabelsY);
        MagnifierGroup.Visibility = Visibility.Visible;
        MagnifierCoordsBorder.Visibility = Visibility.Visible;
    }

    public void HideMagnifier()
    {
        MagnifierGroup.Visibility = Visibility.Hidden;
        MagnifierCoordsBorder.Visibility = Visibility.Hidden;
    }

    // ---- Toolbar ----------------------------------------------------------------------------

    /// <summary>Size of the toolbar in physical pixels on THIS monitor's DPI (it is laid out in
    /// DIPs, so the same toolbar is a different size on each monitor), with the design size as the
    /// fallback before the first layout pass.</summary>
    public Size ToolbarSizePx()
    {
        Toolbar.UpdateLayout();
        var scale = DpiScale;
        var w = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 600;
        var h = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 60;
        return new Size(w * scale, h * scale);
    }

    /// <summary>Show the toolbar with its top-left at <paramref name="topLeftPx"/>.</summary>
    public void ShowToolbarAt(Point topLeftPx)
    {
        var d = ToDips(topLeftPx.X, topLeftPx.Y);
        Canvas.SetLeft(Toolbar, d.X);
        Canvas.SetTop(Toolbar, d.Y);
        Toolbar.Visibility = Visibility.Visible;
    }

    public void HideToolbar() => Toolbar.Visibility = Visibility.Hidden;

    public void SetStatus(string text, bool applyEnabled)
    {
        StatusLabel.Text = text;
        ToolbarApplyButton.IsEnabled = applyEnabled;
    }

    public void SetZoomText(string text) => ZoomLabel.Text = text;

    // ---- Input, forwarded to the session in physical screen pixels --------------------------

    private Point ScreenPx(MouseEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        return new Point(Math.Round(p.X), Math.Round(p.Y));
    }

    /// <summary>Preview MouseDown on the toolbar background. Walks up from the original source to
    /// detect Button hits: buttons keep their click semantics; everything else (TextBlocks,
    /// dividers, the Border itself) starts a drag. The mouse is captured on the root element of
    /// this view (never on a child that can hide), so the drag goes on over the other monitors.</summary>
    private void OnToolbarPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonAncestor(e.OriginalSource as DependencyObject)) return;
        if (_session.BeginToolbarDrag(this, e.GetPosition(Toolbar), ScreenPx(e))) RootGrid.CaptureMouse();
        e.Handled = true;
    }

    private static bool IsButtonAncestor(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_session.MouseDown(this, ScreenPx(e), e.ClickCount)) RootGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => _session.MouseMove(this, ScreenPx(e));

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RootGrid.ReleaseMouseCapture();
        _session.MouseUp(this, ScreenPx(e));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _session.Zoom(e.Delta);
        e.Handled = true;
    }

    /// <summary>Wired to both PreviewKeyDown (tunnel) and KeyDown (bubble) so Esc / Enter / Delete
    /// reach the session even when a child element has keyboard focus and would swallow the key.
    /// Both edges hit the same router, idempotent because every branch sets e.Handled when it acts.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e) => _session.HandleKey(e);

    private void OnToolbarApplyClicked(object sender, RoutedEventArgs e) => _session.Apply();

    private void OnToolbarCancelClicked(object sender, RoutedEventArgs e) => _session.Cancel();

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
