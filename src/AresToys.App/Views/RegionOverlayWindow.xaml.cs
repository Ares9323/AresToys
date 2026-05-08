using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AresToys.Capture;
using AresToys.Storage.Settings;

namespace AresToys.App.Views;

public partial class RegionOverlayWindow : Window
{
    private Point? _dragStart;
    private CaptureRegion? _result;

    /// <summary>Committed regions accumulated by successive drags before the user confirms.
    /// In overlay-canvas DIP coordinates (same space as <c>OverlayCanvas</c>) so the dim
    /// path + per-rect borders can use them directly. Empty until the first mouse-up;
    /// confirmation (Enter / double-click after first region) collapses the list into a
    /// composite PNG via <see cref="BuildMultiRegionComposite"/>.</summary>
    private readonly List<Rect> _committedRegions = new();
    /// <summary>Index into <see cref="_committedRegions"/> of the rect the user has clicked
    /// to select, or -1 if nothing selected. Selected = highlighted border + Delete removes
    /// it. Same UX as the editor's pending-crop list.</summary>
    private int _selectedRegionIndex = -1;
    /// <summary>Per-rect Rectangle UI elements indexed parallel to
    /// <see cref="_committedRegions"/>. Held in a list so we can update individual borders
    /// (selection state) without rebuilding the whole overlay each tick.</summary>
    private readonly List<System.Windows.Shapes.Rectangle> _committedRegionRects = new();
    private int _magnifierHalf = 8;            // 17×17 sample by default
    private const int MagnifierBoxPx = 160;    // displayed magnifier size in DIPs
    private const int MagnifierCursorOffset = 24;
    private BitmapSource? _screenSnapshot;
    private int _screenSnapshotLeft, _screenSnapshotTop;

    /// <summary>When true, the overlay closes on the FIRST committed region (single drag
    /// or one snap-to-window click) without waiting for Enter — single-shot semantics that
    /// match the pre-multi-region behaviour. Set by callers (e.g. the CaptureRegionTask
    /// pipeline task) per workflow. Default false = current multi-region UX.</summary>
    public bool AutoConfirmOnFirstSelection { get; set; }

    /// <summary>PNG bytes of the picked region, cropped from the screen snapshot the
    /// overlay took at open-time. Non-null when <see cref="PickRegion"/> returned a
    /// region — callers can use these directly and skip a second BitBlt round-trip.
    /// Why: the freeze-then-crop flow (ShareX-style) eliminates the gap between the
    /// user's mouse-up and the capture, so transient UI (open dropdowns, hover popups)
    /// stays visible in the screenshot exactly as the user saw it during selection.</summary>
    public byte[]? PickedSnapshotBytes { get; private set; }
    private System.Windows.Threading.DispatcherTimer? _magnifierTimer;
    private int _lastMagnifierX = int.MinValue, _lastMagnifierY = int.MinValue;
    // Top-level windows enumerated once when the overlay opens. Used for snap-to-window.
    private IReadOnlyList<WindowSnapshot> _windows = Array.Empty<WindowSnapshot>();
    private WindowSnapshot? _hoveredWindow;

    /// <summary>Snapshot the entire virtual screen RIGHT NOW and return a frozen
    /// <see cref="BitmapSource"/> alongside its origin in physical pixels. Static / public
    /// so callers can capture at the EARLIEST point in their pipeline (before any focus
    /// shift caused by clicking a tray menu, opening a window, etc.) and pass the result
    /// into <see cref="RegionOverlayWindow"/>'s constructor — same trick ShareX uses to
    /// keep open dropdowns / hover popups visible in the captured image. Returns
    /// <c>(null, 0, 0)</c> on Win32 failure; callers can fall back to letting the overlay
    /// take its own snapshot.</summary>
    public static (BitmapSource? Snapshot, int Left, int Top) CaptureVirtualScreen()
    {
        var (left, top, w, h) = VirtualScreen.GetBounds();
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h));
            }
            var hbm = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return (src, left, top);
            }
            finally { _ = DeleteObject(hbm); }
        }
        catch (System.ComponentModel.Win32Exception) { return (null, left, top); }
        catch (ArgumentException) { return (null, left, top); }
    }

    public RegionOverlayWindow() : this(null, 0, 0) { }

    /// <summary>Construct the overlay with a pre-captured snapshot. Pass null to have the
    /// overlay take its own snapshot at <see cref="PickRegion"/> time (back-compat path).</summary>
    public RegionOverlayWindow(BitmapSource? prefabSnapshot, int prefabLeft, int prefabTop)
    {
        if (prefabSnapshot is not null)
        {
            _screenSnapshot = prefabSnapshot;
            _screenSnapshotLeft = prefabLeft;
            _screenSnapshotTop = prefabTop;
        }
        InitializeComponent();

        // SystemParameters.VirtualScreen* returns DIPs (DPI-aware), unlike VirtualScreen.GetBounds()
        // which returns physical pixels. Mixing the two on scaling ≠ 100% makes the window too big
        // and crops content off the right/bottom.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        // Toolbar drag: handled at Preview level so the click is consumed before the
        // window-level OnMouseDown decides to start a region drag. Buttons inside the
        // toolbar opt out via the OriginalSource walk-up in OnToolbarPreviewMouseDown.
        Toolbar.PreviewMouseLeftButtonDown += OnToolbarPreviewMouseDown;
        Loaded += async (_, _) =>
        {
            Activate(); Focus(); Cursor = Cursors.Cross;
            MagnifierCircle.Width = MagnifierCircle.Height = MagnifierBoxPx;
            UpdateDim(0, 0, 0, 0);
            // Auto-confirm mode hides the multi-region toolbar entirely — the affordances it
            // exposes (region count, Apply, Cancel for accumulated rects) are meaningless when
            // the overlay closes on the first mouse-up. The Esc shortcut still cancels.
            if (AutoConfirmOnFirstSelection)
            {
                Toolbar.Visibility = Visibility.Collapsed;
            }
            else
            {
                await PositionToolbarFromSettingsOrDefaultAsync().ConfigureAwait(true);
            }
            // Drive magnifier + cursor coords from a 60Hz timer instead of MouseMove (which fires 200+/s
            // on high-poll devices and causes redraw-time lag).
            _magnifierTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _magnifierTimer.Tick += (_, _) => TickMagnifier();
            _magnifierTimer.Start();
        };
        Closed += (_, _) => _magnifierTimer?.Stop();
    }

    /// <summary>Toolbar drag state — when set, the toolbar is being moved by the user and
    /// window-level OnMouseMove updates Canvas.Left/Top instead of running the rect-draw
    /// or region-edit flow.</summary>
    private bool _toolbarDragging;
    private Point _toolbarDragStartCursor; // OverlayCanvas DIPs at MouseDown
    private double _toolbarDragStartLeft;
    private double _toolbarDragStartTop;
    private const string ToolbarLeftSettingKey = "RegionOverlay_ToolbarX";
    private const string ToolbarTopSettingKey  = "RegionOverlay_ToolbarY";

    /// <summary>Preview MouseDown on the toolbar background. Walks up from the original
    /// source to detect Button hits — buttons keep their click semantics; everything else
    /// (TextBlocks, dividers, the Border itself) starts a drag.</summary>
    private void OnToolbarPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonAncestor(e.OriginalSource as DependencyObject)) return;
        _toolbarDragging = true;
        _toolbarDragStartCursor = e.GetPosition(OverlayCanvas);
        _toolbarDragStartLeft = Canvas.GetLeft(Toolbar);
        _toolbarDragStartTop  = Canvas.GetTop(Toolbar);
        if (double.IsNaN(_toolbarDragStartLeft)) _toolbarDragStartLeft = 0;
        if (double.IsNaN(_toolbarDragStartTop))  _toolbarDragStartTop = 0;
        Toolbar.CaptureMouse();
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

    /// <summary>Default position: centered horizontally, 32 DIPs above the bottom edge of
    /// the overlay (matches the previous fixed-position toolbar placement). Persisted
    /// position overrides this when found in settings.</summary>
    private void PositionToolbarAtDefault()
    {
        var w = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 600;
        var h = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 60;
        Canvas.SetLeft(Toolbar, Math.Max(0, (ActualWidth  - w) / 2));
        Canvas.SetTop (Toolbar, Math.Max(0,  ActualHeight - h - 32));
    }

    /// <summary>Read persisted X/Y from settings and apply, falling back to the default
    /// centered-bottom layout when no entry exists or the stored coords would land the
    /// toolbar off-screen (e.g. user persisted on a 4K monitor and now is on a laptop).</summary>
    private async System.Threading.Tasks.Task PositionToolbarFromSettingsOrDefaultAsync()
    {
        // Force a layout pass so ActualWidth/Height of the toolbar are known before we
        // measure against the window bounds.
        Toolbar.UpdateLayout();
        var settings = ResolveSettingsStore();
        if (settings is null) { PositionToolbarAtDefault(); return; }
        try
        {
            var rawX = await settings.GetAsync(ToolbarLeftSettingKey, default).ConfigureAwait(true);
            var rawY = await settings.GetAsync(ToolbarTopSettingKey,  default).ConfigureAwait(true);
            if (rawX is null || rawY is null) { PositionToolbarAtDefault(); return; }
            if (!double.TryParse(rawX, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(rawY, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                PositionToolbarAtDefault();
                return;
            }
            var w = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 600;
            var h = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 60;
            // Reject coords that would push the toolbar entirely off-screen — keeps the
            // overlay usable when the previous session was on a different monitor layout.
            if (x < -w + 40 || x > ActualWidth - 40 || y < -h + 40 || y > ActualHeight - 40)
            {
                PositionToolbarAtDefault();
                return;
            }
            Canvas.SetLeft(Toolbar, x);
            Canvas.SetTop (Toolbar, y);
        }
        catch
        {
            PositionToolbarAtDefault();
        }
    }

    private void PersistToolbarPosition()
    {
        var settings = ResolveSettingsStore();
        if (settings is null) return;
        var x = Canvas.GetLeft(Toolbar);
        var y = Canvas.GetTop (Toolbar);
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        // Fire-and-forget — the overlay closes shortly after a drag, no value in awaiting.
        _ = settings.SetAsync(ToolbarLeftSettingKey, x.ToString("R", CultureInfo.InvariantCulture), sensitive: false, default);
        _ = settings.SetAsync(ToolbarTopSettingKey,  y.ToString("R", CultureInfo.InvariantCulture), sensitive: false, default);
    }

    private static ISettingsStore? ResolveSettingsStore()
    {
        if (System.Windows.Application.Current is App app) return app.Services.GetService<ISettingsStore>();
        return null;
    }

    private void TickMagnifier()
    {
        // Consume any pending drag rect cached by OnMouseMove. The dim path is the most
        // expensive piece (rebuilds a GeometryGroup with N + 1 figures); doing it here at
        // 60 Hz instead of per pointer event (up to 1000 Hz on gaming mice) is what keeps
        // the crosshair / magnifier latency-free during a drag.
        //
        // Two semantics live in this cache: (a) NEW rect being drawn — pass it as the
        // draggingRect argument so it's added as an extra hole on top of the committed
        // list; (b) MOVE/RESIZE on a committed rect — _committedRegions already holds the
        // updated geometry, so a plain rebuild is correct (passing it explicitly would
        // duplicate the rect and even-odd would cancel the hole).
        if (_pendingDragRect is { } pendingRect)
        {
            if (_dragStart is not null) UpdateMultiDim(pendingRect);
            else UpdateMultiDim();
            _pendingDragRect = null;
        }

        if (!GetCursorPos(out var pt)) return;
        if (pt.X == _lastMagnifierX && pt.Y == _lastMagnifierY) return;
        _lastMagnifierX = pt.X;
        _lastMagnifierY = pt.Y;

        CursorPosLabel.Text = $"X: {pt.X}  Y: {pt.Y}";
        CursorPosBorder.Visibility = Visibility.Visible;
        PositionInBottomRight(CursorPosBorder);

        var cursorInWindow = PointFromScreen(new Point(pt.X, pt.Y));
        UpdateMagnifier(pt.X, pt.Y, cursorInWindow);
        UpdateSnapPreview(pt.X, pt.Y);
    }

    /// <summary>While idle (no drag), highlight the window under the cursor so the user knows that
    /// a click without drag will snap-capture it.</summary>
    private void UpdateSnapPreview(int physicalX, int physicalY)
    {
        if (_dragStart is not null)
        {
            SnapRect.Visibility = Visibility.Collapsed;
            _hoveredWindow = null;
            return;
        }
        var hover = WindowEnumeration.FindWindowAt(physicalX, physicalY, _windows);
        _hoveredWindow = hover;
        if (hover is null)
        {
            SnapRect.Visibility = Visibility.Collapsed;
            return;
        }
        // Convert physical pixels (screen-space) to overlay-canvas DIPs.
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = hover.X / dpi.DpiScaleX - Left;
        var y = hover.Y / dpi.DpiScaleY - Top;
        var w = hover.Width / dpi.DpiScaleX;
        var h = hover.Height / dpi.DpiScaleY;
        Canvas.SetLeft(SnapRect, x);
        Canvas.SetTop(SnapRect, y);
        SnapRect.Width = Math.Max(0, w);
        SnapRect.Height = Math.Max(0, h);
        SnapRect.Visibility = Visibility.Visible;
    }

    public CaptureRegion? PickRegion()
    {
        // If the caller pre-supplied a snapshot via the constructor, skip the internal
        // capture — the prefab was taken at a moment that respected the user's UI state
        // (e.g. before any focus shift could close an open dropdown). Otherwise capture
        // here as a fallback.
        if (_screenSnapshot is null) TakeScreenSnapshot();
        if (_screenSnapshot is not null) ScreenshotImage.Source = _screenSnapshot;
        // Enumerate visible windows for snap-on-hover. EnumWindows returns top-most z-order first
        // (which is what we want — clicking on overlapping windows snaps to the foreground one).
        _windows = WindowEnumeration.EnumerateVisibleWindows();
        ShowDialog();
        return _result;
    }

    private void TakeScreenSnapshot()
    {
        var (left, top, w, h) = VirtualScreen.GetBounds();
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h));
            }
            var hbm = bmp.GetHbitmap();
            try
            {
                _screenSnapshot = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                _screenSnapshot.Freeze();
                _screenSnapshotLeft = left;
                _screenSnapshotTop = top;
            }
            finally { _ = DeleteObject(hbm); }
        }
        catch (System.ComponentModel.Win32Exception) { _screenSnapshot = null; }
        catch (ArgumentException) { _screenSnapshot = null; }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = null;
            Close();
            e.Handled = true;
            return;
        }
        // Enter / Return: confirm all committed regions and produce the final composite.
        // Empty list = no-op (user has only seen the empty overlay; let them keep dragging).
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_committedRegions.Count > 0)
            {
                ConfirmCommittedRegions();
                e.Handled = true;
            }
            return;
        }
        // Delete removes the currently-selected committed region. Same UX as the editor's
        // pending-crop list. After delete the selection clears (no auto-pick of an adjacent
        // rect — user re-clicks if they want another selected).
        if (e.Key == Key.Delete && _selectedRegionIndex >= 0 && _selectedRegionIndex < _committedRegions.Count)
        {
            _committedRegions.RemoveAt(_selectedRegionIndex);
            OverlayCanvas.Children.Remove(_committedRegionRects[_selectedRegionIndex]);
            _committedRegionRects.RemoveAt(_selectedRegionIndex);
            _selectedRegionIndex = -1;
            UpdateMultiDim();
            UpdateRegionSelectionVisuals();
            UpdateToolbarStatus();
            e.Handled = true;
        }
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        // Grip handles claim the click first via their own PreviewMouseLeftButtonDown
        // (see EnsureGripHandlesCreated) and short-circuit before we get here. So if we're
        // running, the click missed every grip — fall through to rect / empty-canvas logic.
        var pt = e.GetPosition(OverlayCanvas);

        // Click on a committed region: select it AND start a move-drag in one gesture, the
        // way ShareX does. Walk top-down so the most-recent rect wins on overlap. The
        // selected rect renders amber + grip handles, Delete removes it.
        for (var i = _committedRegions.Count - 1; i >= 0; i--)
        {
            if (!_committedRegions[i].Contains(pt)) continue;
            _selectedRegionIndex = i;
            UpdateRegionSelectionVisuals();
            BeginRegionDrag(RegionDragMode.Move, i, pt);
            e.Handled = true;
            return;
        }
        _selectedRegionIndex = -1;
        UpdateRegionSelectionVisuals();

        _dragStart = pt;
        Canvas.SetLeft(SelectionRect, _dragStart.Value.X);
        Canvas.SetTop(SelectionRect, _dragStart.Value.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
        SizeLabelBorder.Visibility = Visibility.Visible;
        SnapRect.Visibility = Visibility.Collapsed;
        // While dragging the cursor-only label is hidden; the selection label takes its place.
        CursorPosBorder.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        var current = e.GetPosition(OverlayCanvas);

        // Toolbar drag preempts everything else — moving the toolbar shouldn't draw a new
        // rect or move a region.
        if (_toolbarDragging)
        {
            var dx = current.X - _toolbarDragStartCursor.X;
            var dy = current.Y - _toolbarDragStartCursor.Y;
            Canvas.SetLeft(Toolbar, _toolbarDragStartLeft + dx);
            Canvas.SetTop (Toolbar, _toolbarDragStartTop  + dy);
            return;
        }

        // Crosshair position update is cheap (4 line-endpoint assignments) and runs every
        // pointer event so the alignment guide tracks the cursor in real time. The expensive
        // dim-path rebuild is throttled separately via _pendingDragRect (handled by the tick).
        UpdateCrosshairPosition(current);

        // Move / resize on a committed region preempts the new-rect drag flow below.
        if (_regionDragMode != RegionDragMode.None)
        {
            ApplyRegionDrag(current);
            return;
        }

        if (_dragStart is null) return;

        var x = Math.Min(_dragStart.Value.X, current.X);
        var y = Math.Min(_dragStart.Value.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.Value.X);
        var h = Math.Abs(current.Y - _dragStart.Value.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        // Defer the dim path rebuild to the next magnifier tick (~16 ms throttle) so the
        // 1000 Hz mouse-move stream doesn't queue up GeometryGroup rebuilds and starve
        // the timer-driven crosshair / magnifier updates. The cached rect is consumed by
        // TickMagnifier once per frame.
        _pendingDragRect = new Rect(x, y, w, h);

        var (px0, py0) = ToPhysicalPixels(x, y);
        var pxW = ToPhysicalSize(w, horizontal: true);
        var pxH = ToPhysicalSize(h, horizontal: false);
        SizeLabel.Text = $"X: {px0}  Y: {py0}  W: {pxW}  H: {pxH}";
        Canvas.SetLeft(SizeLabelBorder, x + 6);
        Canvas.SetTop(SizeLabelBorder, y + 6);
    }

    /// <summary>Rect cached from the latest OnMouseMove during a drag — applied to the dim
    /// path once per magnifier tick (60 Hz) instead of on every pointer event (up to
    /// 1000 Hz). Keeps the dispatcher responsive and the crosshair / magnifier smooth.</summary>
    private Rect? _pendingDragRect;

    /// <summary>What kind of edit is happening on the currently-selected committed region.
    /// Move = drag the whole rect; the eight compass directions = resize from that handle.
    /// Mirrors the editor's pending-crop drag pattern so the UX is identical between the two
    /// surfaces (region overlay during capture, editor crop tool after capture).</summary>
    private enum RegionDragMode { None, Move, NW, N, NE, E, SE, S, SW, W }
    private RegionDragMode _regionDragMode = RegionDragMode.None;
    /// <summary>Index into <see cref="_committedRegions"/> being dragged / resized. Captured
    /// at MouseDown so subsequent selection changes don't disturb the in-flight edit.</summary>
    private int _regionDragIndex = -1;
    private Point _regionDragStart;
    private Rect _regionDragOriginal;
    /// <summary>Eight grip handles overlaid on the selected committed region (4 corners +
    /// 4 edge midpoints). Created lazily on first selection then kept around — visibility
    /// toggles instead of recreating each time so the canvas tree stays stable.</summary>
    private readonly System.Windows.Shapes.Rectangle[] _gripHandles = new System.Windows.Shapes.Rectangle[8];
    private static readonly RegionDragMode[] _gripModes =
    {
        RegionDragMode.NW, RegionDragMode.N, RegionDragMode.NE,
        RegionDragMode.W,                     RegionDragMode.E,
        RegionDragMode.SW, RegionDragMode.S, RegionDragMode.SE,
    };

    /// <summary>Single-rect convenience wrapper used at startup (full dim, no hole). The
    /// multi-rect path lives in <see cref="UpdateMultiDim"/>.</summary>
    private void UpdateDim(double x, double y, double w, double h) =>
        UpdateMultiDim(new Rect(x, y, w, h));

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        // Wheel up: more zoom (smaller sample → bigger pixels). Range [3, 20] half = [7, 41] sample.
        _magnifierHalf = Math.Clamp(_magnifierHalf + (e.Delta > 0 ? -1 : 1), 3, 20);
        // Invalidate the TickMagnifier cache so the next frame re-renders with the new
        // sample size — without this the user could spin the wheel forever and see no
        // visual change until they nudged the mouse.
        _lastMagnifierX = int.MinValue;
        if (ZoomLabel is not null)
            ZoomLabel.Text = $"Zoom: {(_magnifierHalf * 2 + 1)}px";
        e.Handled = true;
    }

    private void UpdateMagnifier(int physicalX, int physicalY, Point cursorInWindow)
    {
        var sampleSize = _magnifierHalf * 2 + 1;
        var snap = _screenSnapshot;
        if (snap is not null)
        {
            // Crop relative to the snapshot's origin (= virtual-screen left/top).
            var rx = physicalX - _magnifierHalf - _screenSnapshotLeft;
            var ry = physicalY - _magnifierHalf - _screenSnapshotTop;
            if (rx < 0) rx = 0;
            if (ry < 0) ry = 0;
            if (rx + sampleSize > snap.PixelWidth) rx = snap.PixelWidth - sampleSize;
            if (ry + sampleSize > snap.PixelHeight) ry = snap.PixelHeight - sampleSize;
            if (rx >= 0 && ry >= 0 && sampleSize > 0)
            {
                // Wire the cropped bitmap into the ImageBrush feeding the circular Ellipse.
                // Setting ImageBrush.ImageSource is preferred over recreating the brush so
                // WPF reuses the same paint; cheap on every magnifier tick.
                MagnifierImage.Source = new CroppedBitmap(snap, new Int32Rect(rx, ry, sampleSize, sampleSize));
            }
        }
        var pixSize = (double)MagnifierBoxPx / sampleSize;
        MagnifierCrosshair.Width = pixSize;
        MagnifierCrosshair.Height = pixSize;

        MagnifierCoords.Text = $"X: {physicalX}  Y: {physicalY}";
        MagnifierGroup.Visibility = Visibility.Visible;
        MagnifierCoordsBorder.Visibility = Visibility.Visible;
        PositionMagnifierNearCursor(cursorInWindow);
        // Position is also updated from OnMouseMove for snappy real-time tracking; here we
        // just animate the dashes so the visual phase scrolls.
        UpdateCrosshairPosition(cursorInWindow);
        UpdateCrosshairAnts();
    }

    /// <summary>Position-only crosshair update — cheap, runs from OnMouseMove at 1000 Hz on
    /// gaming mice without starving the dispatcher. The dash-offset animation is decoupled
    /// (see <see cref="UpdateCrosshairAnts"/>) so we don't pay for dash recomputation on
    /// every pointer event. Splitting them eliminated the visible lag we had when the
    /// crosshair tried to do both per-pointer and per-tick work in the same call.</summary>
    private void UpdateCrosshairPosition(Point cursorInWindow)
    {
        CrosshairH.X1 = 0; CrosshairH.X2 = ActualWidth;
        CrosshairH.Y1 = CrosshairH.Y2 = cursorInWindow.Y;
        CrosshairHDark.X1 = 0; CrosshairHDark.X2 = ActualWidth;
        CrosshairHDark.Y1 = CrosshairHDark.Y2 = cursorInWindow.Y;
        CrosshairV.Y1 = 0; CrosshairV.Y2 = ActualHeight;
        CrosshairV.X1 = CrosshairV.X2 = cursorInWindow.X;
        CrosshairVDark.Y1 = 0; CrosshairVDark.Y2 = ActualHeight;
        CrosshairVDark.X1 = CrosshairVDark.X2 = cursorInWindow.X;

        CrosshairH.Visibility = Visibility.Visible;
        CrosshairHDark.Visibility = Visibility.Visible;
        CrosshairV.Visibility = Visibility.Visible;
        CrosshairVDark.Visibility = Visibility.Visible;
    }

    /// <summary>Animation-only update — bumps the marching-ants phase. Driven from the
    /// magnifier tick (60 Hz) which is plenty for the dash scroll illusion.</summary>
    private void UpdateCrosshairAnts()
    {
        _crosshairAntsPhase = (_crosshairAntsPhase + 0.5) % 8.0;
        CrosshairH.StrokeDashOffset = _crosshairAntsPhase;
        CrosshairV.StrokeDashOffset = _crosshairAntsPhase;
        CrosshairHDark.StrokeDashOffset = _crosshairAntsPhase + 4;
        CrosshairVDark.StrokeDashOffset = _crosshairAntsPhase + 4;
    }

    private double _crosshairAntsPhase;

    private void PositionMagnifierNearCursor(Point cursor)
    {
        const double margin = 8;
        var w = MagnifierGroup.ActualWidth > 0 ? MagnifierGroup.ActualWidth : MagnifierBoxPx;
        var h = MagnifierGroup.ActualHeight > 0 ? MagnifierGroup.ActualHeight : MagnifierBoxPx;

        // Default: bottom-right of cursor; flip to other side near edges.
        var x = cursor.X + MagnifierCursorOffset;
        var y = cursor.Y + MagnifierCursorOffset;
        if (x + w + margin > ActualWidth) x = cursor.X - MagnifierCursorOffset - w;
        if (y + h + margin > ActualHeight) y = cursor.Y - MagnifierCursorOffset - h;
        if (x < margin) x = margin;
        if (y < margin) y = margin;
        Canvas.SetLeft(MagnifierGroup, x);
        Canvas.SetTop(MagnifierGroup, y);

        // Coords label sits just below the magnifier circle.
        var labelW = MagnifierCoordsBorder.ActualWidth > 0 ? MagnifierCoordsBorder.ActualWidth : 120;
        Canvas.SetLeft(MagnifierCoordsBorder, x + (w - labelW) / 2);
        Canvas.SetTop(MagnifierCoordsBorder, y + h + 4);
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private (int X, int Y) ToPhysicalPixels(double dipX, double dipY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var (vsLeft, vsTop, _, _) = VirtualScreen.GetBounds();
        return ((int)Math.Round(dipX * dpi.DpiScaleX) + vsLeft,
                (int)Math.Round(dipY * dpi.DpiScaleY) + vsTop);
    }

    private int ToPhysicalSize(double dipSize, bool horizontal)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)Math.Round(dipSize * (horizontal ? dpi.DpiScaleX : dpi.DpiScaleY));
    }

    private void PositionInBottomRight(System.Windows.FrameworkElement el)
    {
        const double margin = 16;
        var w = el.ActualWidth > 0 ? el.ActualWidth : 160;
        var h = el.ActualHeight > 0 ? el.ActualHeight : 30;
        Canvas.SetLeft(el, ActualWidth - w - margin);
        Canvas.SetTop(el, ActualHeight - h - margin);
    }

    private void OnMouseUp(object? sender, MouseButtonEventArgs e)
    {
        // End an in-flight toolbar drag: release capture + persist the new position so the
        // next overlay session opens with the toolbar where the user left it.
        if (_toolbarDragging)
        {
            _toolbarDragging = false;
            Toolbar.ReleaseMouseCapture();
            PersistToolbarPosition();
            e.Handled = true;
            return;
        }

        // End an in-flight move/resize on a committed region: just clear state — the rect
        // was already mutated incrementally by ApplyRegionDrag, and the dim path has been
        // updated on every tick. Nothing to "commit" here.
        if (_regionDragMode != RegionDragMode.None)
        {
            _regionDragMode = RegionDragMode.None;
            _regionDragIndex = -1;
            ReleaseMouseCapture();
            // Make sure the dim hole reflects the final position even if the last
            // pending-rect tick hasn't fired yet.
            UpdateMultiDim();
            UpdateGripsForSelectedRegion();
            e.Handled = true;
            return;
        }

        if (_dragStart is null) return;
        ReleaseMouseCapture();

        var current = e.GetPosition(OverlayCanvas);
        var rawW = Math.Abs(current.X - _dragStart.Value.X);
        var rawH = Math.Abs(current.Y - _dragStart.Value.Y);
        var rawX = Math.Min(_dragStart.Value.X, current.X);
        var rawY = Math.Min(_dragStart.Value.Y, current.Y);
        _dragStart = null;

        // Click without (significant) drag: snap-capture the window under the cursor — but
        // ADD to the committed list rather than closing immediately. Multi-region: the user
        // can click multiple windows / drag multiple rects, then Enter to confirm all.
        if (rawW < 5 && rawH < 5 && _hoveredWindow is { } w)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // Convert hovered-window physical-pixel bounds to overlay-canvas DIP coords.
            var dipRect = new Rect(
                w.X / dpi.DpiScaleX - Left,
                w.Y / dpi.DpiScaleY - Top,
                w.Width / dpi.DpiScaleX,
                w.Height / dpi.DpiScaleY);
            CommitRegion(dipRect);
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeLabelBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionRect.Visibility = Visibility.Collapsed;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
        if (rawW < 1 || rawH < 1) return;
        // Drag commits a rect. Stays open so the user can add more regions.
        CommitRegion(new Rect(rawX, rawY, rawW, rawH));
    }

    /// <summary>Append a new region to <see cref="_committedRegions"/> + render its border
    /// rectangle in OverlayCanvas + recompute the dim mask so the new rect "shows through"
    /// the dim. Auto-selects the just-added region for Delete-key convenience.</summary>
    private void CommitRegion(Rect dipRect)
    {
        _committedRegions.Add(dipRect);
        _selectedRegionIndex = _committedRegions.Count - 1;
        var border = new System.Windows.Shapes.Rectangle
        {
            Width = dipRect.Width,
            Height = dipRect.Height,
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(border, dipRect.X);
        Canvas.SetTop(border, dipRect.Y);
        OverlayCanvas.Children.Add(border);
        _committedRegionRects.Add(border);
        UpdateMultiDim();
        UpdateRegionSelectionVisuals();
        UpdateToolbarStatus();
        // Auto-confirm mode: every commit path (drag mouse-up, snap-to-window click,
        // anything that lands here) collapses to "first valid region wins". One call
        // site is enough because OnMouseUp / snap-click both funnel through CommitRegion.
        if (AutoConfirmOnFirstSelection)
        {
            ConfirmCommittedRegions();
        }
    }

    /// <summary>Refresh the bottom toolbar's status text + Apply button enabled state.
    /// Called on every region commit / remove so the count stays in sync. Idempotent —
    /// safe to call repeatedly.</summary>
    private void UpdateToolbarStatus()
    {
        if (StatusLabel is null) return;
        var count = _committedRegions.Count;
        StatusLabel.Text = count switch
        {
            0 => "No regions yet — drag to start",
            1 => "1 region · ready to apply",
            _ => $"{count} regions · ready to apply",
        };
        ToolbarApplyButton.IsEnabled = count > 0;
    }

    private void OnToolbarApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_committedRegions.Count > 0) ConfirmCommittedRegions();
    }

    private void OnToolbarCancelClicked(object sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    /// <summary>Rebuild the dim mask path with one hole per committed region (+ the in-flight
    /// drag rect when a drag is active). Even-odd fill: every rect cuts a hole through the
    /// outer dim; overlapping rects don't double-darken.</summary>
    private void UpdateMultiDim(Rect? draggingRect = null)
    {
        var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
        combined.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var r in _committedRegions) combined.Children.Add(new RectangleGeometry(r));
        if (draggingRect is { } d) combined.Children.Add(new RectangleGeometry(d));
        combined.Freeze();
        DimPath.Data = combined;
    }

    /// <summary>Highlight the selected region's border (amber) vs unselected (white) so the
    /// user sees what Delete will hit. Cheap — just toggles Stroke color on the cached rects.</summary>
    private void UpdateRegionSelectionVisuals()
    {
        for (var i = 0; i < _committedRegionRects.Count; i++)
        {
            var sel = i == _selectedRegionIndex;
            _committedRegionRects[i].Stroke = sel
                ? new SolidColorBrush(Color.FromRgb(255, 196, 0))
                : System.Windows.Media.Brushes.White;
            _committedRegionRects[i].StrokeThickness = sel ? 2.0 : 1.5;
        }
        UpdateGripsForSelectedRegion();
    }

    /// <summary>Lazily build the eight grip rectangles on first use and wire each to a
    /// PreviewMouseLeftButtonDown handler that starts a resize drag in the right mode. The
    /// grips live in OverlayCanvas alongside the rect borders; sized in DIPs (no zoom in
    /// the overlay) so a fixed 10-px target stays comfortable on any DPI.</summary>
    private void EnsureGripHandlesCreated()
    {
        if (_gripHandles[0] is not null) return;
        const double gripSize = 10;
        for (var i = 0; i < 8; i++)
        {
            var idx = i; // capture
            var grip = new System.Windows.Shapes.Rectangle
            {
                Width = gripSize,
                Height = gripSize,
                Fill = System.Windows.Media.Brushes.White,
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                Cursor = CursorForMode(_gripModes[idx]),
            };
            grip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_selectedRegionIndex < 0) return;
                BeginRegionDrag(_gripModes[idx], _selectedRegionIndex, e.GetPosition(OverlayCanvas));
                e.Handled = true;
            };
            _gripHandles[i] = grip;
            OverlayCanvas.Children.Add(grip);
        }
    }

    private static Cursor CursorForMode(RegionDragMode m) => m switch
    {
        RegionDragMode.NW or RegionDragMode.SE => Cursors.SizeNWSE,
        RegionDragMode.NE or RegionDragMode.SW => Cursors.SizeNESW,
        RegionDragMode.N  or RegionDragMode.S  => Cursors.SizeNS,
        RegionDragMode.E  or RegionDragMode.W  => Cursors.SizeWE,
        _ => Cursors.Arrow,
    };

    /// <summary>Position + show the eight grip handles on the selected region, or hide them
    /// all if nothing is selected. Called whenever the rect's geometry or selection state
    /// changes (commit, delete, resize tick, move tick).</summary>
    private void UpdateGripsForSelectedRegion()
    {
        EnsureGripHandlesCreated();
        if (_selectedRegionIndex < 0 || _selectedRegionIndex >= _committedRegions.Count)
        {
            foreach (var g in _gripHandles) g.Visibility = Visibility.Collapsed;
            return;
        }
        var r = _committedRegions[_selectedRegionIndex];
        const double half = 5; // half of 10 px grip
        // Grip centre points in OverlayCanvas DIPs, indexed parallel to _gripModes:
        // NW, N, NE, W, E, SW, S, SE.
        var cx = new[]
        {
            r.Left,            r.Left + r.Width / 2, r.Right,
            r.Left,                                   r.Right,
            r.Left,            r.Left + r.Width / 2, r.Right,
        };
        var cy = new[]
        {
            r.Top,             r.Top,                r.Top,
            r.Top + r.Height / 2,                    r.Top + r.Height / 2,
            r.Bottom,          r.Bottom,             r.Bottom,
        };
        for (var i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_gripHandles[i], cx[i] - half);
            Canvas.SetTop(_gripHandles[i], cy[i] - half);
            Canvas.SetZIndex(_gripHandles[i], 100); // above the dim path / rect borders
            _gripHandles[i].Visibility = Visibility.Visible;
        }
    }

    /// <summary>Capture state needed to interpret subsequent mouse-move deltas as a move
    /// or resize on a specific committed region. Caller is responsible for setting the
    /// selection first (so the grips match the dragged rect).</summary>
    private void BeginRegionDrag(RegionDragMode mode, int index, Point startPoint)
    {
        _regionDragMode = mode;
        _regionDragIndex = index;
        _regionDragStart = startPoint;
        _regionDragOriginal = _committedRegions[index];
        CaptureMouse();
    }

    /// <summary>Apply the in-flight move/resize to the dragged region's geometry, refresh
    /// its border + grips, and queue a dim-path rebuild via _pendingDragRect (the tick
    /// consumes it at 60 Hz so the dispatcher stays responsive at high pointer rates).</summary>
    private void ApplyRegionDrag(Point current)
    {
        if (_regionDragIndex < 0 || _regionDragIndex >= _committedRegions.Count) return;
        var dx = current.X - _regionDragStart.X;
        var dy = current.Y - _regionDragStart.Y;
        var o = _regionDragOriginal;
        double x = o.X, y = o.Y, w = o.Width, h = o.Height;

        if (_regionDragMode == RegionDragMode.Move)
        {
            x = o.X + dx; y = o.Y + dy;
        }
        else
        {
            // Edge / corner resize: each direction tweaks one or both axes.
            switch (_regionDragMode)
            {
                case RegionDragMode.NW: x = o.X + dx; y = o.Y + dy; w = o.Width  - dx; h = o.Height - dy; break;
                case RegionDragMode.N:                y = o.Y + dy;                    h = o.Height - dy; break;
                case RegionDragMode.NE:               y = o.Y + dy; w = o.Width  + dx; h = o.Height - dy; break;
                case RegionDragMode.E:                              w = o.Width  + dx;                    break;
                case RegionDragMode.SE:                             w = o.Width  + dx; h = o.Height + dy; break;
                case RegionDragMode.S:                                                 h = o.Height + dy; break;
                case RegionDragMode.SW: x = o.X + dx;               w = o.Width  - dx; h = o.Height + dy; break;
                case RegionDragMode.W:  x = o.X + dx;               w = o.Width  - dx;                    break;
            }
            // Clamp to a 1-px minimum. We don't flip-on-cross like a paint app would; the
            // user can release and re-drag if they want to invert the rect, and a flip mid-
            // drag would relocate every grip under their cursor mid-gesture.
            if (w < 1) w = 1;
            if (h < 1) h = 1;
        }

        var newRect = new Rect(x, y, w, h);
        _committedRegions[_regionDragIndex] = newRect;
        var border = _committedRegionRects[_regionDragIndex];
        Canvas.SetLeft(border, newRect.X);
        Canvas.SetTop(border, newRect.Y);
        border.Width = newRect.Width;
        border.Height = newRect.Height;
        UpdateGripsForSelectedRegion();
        _pendingDragRect = newRect; // tick rebuilds the dim path with the new geometry
    }

    /// <summary>Bake the committed regions into the final result: bbox = union, output PNG
    /// = bbox dimensions with each region's screen pixels copied in and everything else
    /// transparent. Single-region collapses to a plain crop (transparent halo of zero
    /// thickness around the rect). Single rect ⇒ same output as the legacy single-region
    /// flow; multi rect ⇒ ShareX-style composite.</summary>
    private void ConfirmCommittedRegions()
    {
        if (_committedRegions.Count == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (vsLeft, vsTop, _, _) = VirtualScreen.GetBounds();

        // Translate every DIP rect to absolute physical-pixel coordinates.
        var pixelRects = new List<(int X, int Y, int W, int H)>(_committedRegions.Count);
        foreach (var r in _committedRegions)
        {
            var px = (int)Math.Round(r.X * dpi.DpiScaleX) + vsLeft;
            var py = (int)Math.Round(r.Y * dpi.DpiScaleY) + vsTop;
            var pw = (int)Math.Round(r.Width * dpi.DpiScaleX);
            var ph = (int)Math.Round(r.Height * dpi.DpiScaleY);
            if (pw > 0 && ph > 0) pixelRects.Add((px, py, pw, ph));
        }
        if (pixelRects.Count == 0) return;

        if (pixelRects.Count == 1)
        {
            // Single region: same output as the legacy flow — straight crop, no transparent
            // halo. Keeps the simple-case PNG identical for existing callers / consumers.
            var (sx, sy, sw, sh) = pixelRects[0];
            _result = new CaptureRegion(sx, sy, sw, sh,
                WindowTitle: _hoveredWindow?.Title); // window title only meaningful when single + snap
            PickedSnapshotBytes = TryCropSnapshot(sx, sy, sw, sh);
            Close();
            return;
        }

        // Multi-region: bbox + composite (transparent outside any rect).
        var bx = pixelRects.Min(r => r.X);
        var by = pixelRects.Min(r => r.Y);
        var br = pixelRects.Max(r => r.X + r.W);
        var bb = pixelRects.Max(r => r.Y + r.H);
        var bboxW = br - bx;
        var bboxH = bb - by;
        _result = new CaptureRegion(bx, by, bboxW, bboxH);
        PickedSnapshotBytes = BuildMultiRegionComposite(pixelRects, bx, by, bboxW, bboxH);
        Close();
    }

    /// <summary>Render the final multi-region PNG: bbox-sized transparent canvas with each
    /// region's pixels from the screen snapshot copied in at their bbox-relative offset.
    /// DPI-aware so a 1080p multi-mon mix doesn't get the rects half-resolution.</summary>
    private byte[]? BuildMultiRegionComposite(IReadOnlyList<(int X, int Y, int W, int H)> rects, int bboxX, int bboxY, int bboxW, int bboxH)
    {
        if (_screenSnapshot is null) return null;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var rect in rects)
            {
                // Source coords relative to the snapshot's top-left in the virtual screen,
                // clamped to the snapshot bounds (rects can extend beyond on multi-mon
                // setups when DPI scaling rounds awkwardly).
                int sx = rect.X, sy = rect.Y, sw = rect.W, sh = rect.H;
                int rx = sx - _screenSnapshotLeft;
                int ry = sy - _screenSnapshotTop;
                if (rx < 0) { sw += rx; rx = 0; }
                if (ry < 0) { sh += ry; ry = 0; }
                if (rx + sw > _screenSnapshot.PixelWidth)  sw = _screenSnapshot.PixelWidth - rx;
                if (ry + sh > _screenSnapshot.PixelHeight) sh = _screenSnapshot.PixelHeight - ry;
                if (sw <= 0 || sh <= 0) continue;
                var crop = new CroppedBitmap(_screenSnapshot, new Int32Rect(rx, ry, sw, sh));
                dc.DrawImage(crop, new Rect(sx - bboxX, sy - bboxY, sw, sh));
            }
        }
        var rtb = new RenderTargetBitmap(bboxW, bboxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Crop the screen snapshot to the picked region and PNG-encode it. Returns
    /// null if no snapshot is available (rare — Win32 capture failure at overlay open) or
    /// the requested region falls outside the snapshot bounds. Coordinates are virtual-
    /// screen absolute pixels; the snapshot's top-left is at <c>_screenSnapshotLeft/Top</c>
    /// so we offset before cropping.</summary>
    private byte[]? TryCropSnapshot(int absX, int absY, int w, int h)
    {
        var snap = _screenSnapshot;
        if (snap is null) return null;
        var srcX = absX - _screenSnapshotLeft;
        var srcY = absY - _screenSnapshotTop;
        if (srcX < 0 || srcY < 0 || srcX + w > snap.PixelWidth || srcY + h > snap.PixelHeight)
        {
            return null;
        }
        try
        {
            var cropped = new CroppedBitmap(snap, new Int32Rect(srcX, srcY, w, h));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (ArgumentException) { return null; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }
}
