using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShareQ.Capture;

namespace ShareQ.App.Views;

public partial class RegionOverlayWindow : Window
{
    private Point? _dragStart;
    private CaptureRegion? _result;
    private int _magnifierHalf = 8;            // 17×17 sample by default
    private const int MagnifierBoxPx = 160;    // displayed magnifier size in DIPs
    private const int MagnifierCursorOffset = 24;
    private BitmapSource? _screenSnapshot;
    private int _screenSnapshotLeft, _screenSnapshotTop;

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
        Loaded += (_, _) =>
        {
            Activate(); Focus(); Cursor = Cursors.Cross;
            MagnifierImage.Width = MagnifierImage.Height = MagnifierBoxPx;
            UpdateDim(0, 0, 0, 0);
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

    private void TickMagnifier()
    {
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
        }
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayCanvas);
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
        if (_dragStart is null) return;

        var current = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_dragStart.Value.X, current.X);
        var y = Math.Min(_dragStart.Value.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.Value.X);
        var h = Math.Abs(current.Y - _dragStart.Value.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        UpdateDim(x, y, w, h);

        var (px0, py0) = ToPhysicalPixels(x, y);
        var pxW = ToPhysicalSize(w, horizontal: true);
        var pxH = ToPhysicalSize(h, horizontal: false);
        SizeLabel.Text = $"X: {px0}  Y: {py0}  W: {pxW}  H: {pxH}";
        Canvas.SetLeft(SizeLabelBorder, x + 6);
        Canvas.SetTop(SizeLabelBorder, y + 6);
    }

    /// <summary>Rebuild the dim path so the rect [x,y,w,h] is the "hole" (selection visible),
    /// everything else is dimmed. Called once on load (full dim, hole = 0×0) and on each drag move.</summary>
    private void UpdateDim(double x, double y, double w, double h)
    {
        var outer = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var hole = new RectangleGeometry(new Rect(x, y, w, h));
        var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
        combined.Children.Add(outer);
        combined.Children.Add(hole);
        combined.Freeze();
        DimPath.Data = combined;
    }

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        // Wheel up: more zoom (smaller sample → bigger pixels). Range [3, 20] half = [7, 41] sample.
        _magnifierHalf = Math.Clamp(_magnifierHalf + (e.Delta > 0 ? -1 : 1), 3, 20);
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
            // Clamp to snapshot bounds so cropping near the edge of the virtual screen doesn't throw.
            if (rx < 0) rx = 0;
            if (ry < 0) ry = 0;
            if (rx + sampleSize > snap.PixelWidth) rx = snap.PixelWidth - sampleSize;
            if (ry + sampleSize > snap.PixelHeight) ry = snap.PixelHeight - sampleSize;
            if (rx >= 0 && ry >= 0 && sampleSize > 0)
            {
                MagnifierImage.Source = new CroppedBitmap(snap, new Int32Rect(rx, ry, sampleSize, sampleSize));
            }
        }
        var pixSize = (double)MagnifierBoxPx / sampleSize;
        MagnifierCrosshair.Width = pixSize;
        MagnifierCrosshair.Height = pixSize;

        MagnifierCoords.Text = $"X: {physicalX}  Y: {physicalY}";
        MagnifierBorder.Visibility = Visibility.Visible;
        PositionMagnifierNearCursor(cursorInWindow);
    }

    private void PositionMagnifierNearCursor(Point cursor)
    {
        const double margin = 8;
        var w = MagnifierBorder.ActualWidth > 0 ? MagnifierBorder.ActualWidth : MagnifierBoxPx + 4;
        var h = MagnifierBorder.ActualHeight > 0 ? MagnifierBorder.ActualHeight : MagnifierBoxPx + 40;

        // Default: bottom-right of cursor; flip to other side near edges.
        var x = cursor.X + MagnifierCursorOffset;
        var y = cursor.Y + MagnifierCursorOffset;
        if (x + w + margin > ActualWidth) x = cursor.X - MagnifierCursorOffset - w;
        if (y + h + margin > ActualHeight) y = cursor.Y - MagnifierCursorOffset - h;
        if (x < margin) x = margin;
        if (y < margin) y = margin;
        Canvas.SetLeft(MagnifierBorder, x);
        Canvas.SetTop(MagnifierBorder, y);
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
        if (_dragStart is null) return;
        ReleaseMouseCapture();

        var current = e.GetPosition(OverlayCanvas);
        var dpi = VisualTreeHelper.GetDpi(this);
        var rawW = Math.Abs(current.X - _dragStart.Value.X);
        var rawH = Math.Abs(current.Y - _dragStart.Value.Y);

        // Click without (significant) drag: snap-capture the window under the cursor instead.
        if (rawW < 5 && rawH < 5 && _hoveredWindow is { } w)
        {
            _result = new CaptureRegion(w.X, w.Y, w.Width, w.Height, WindowTitle: w.Title);
            PickedSnapshotBytes = TryCropSnapshot(w.X, w.Y, w.Width, w.Height);
            Close();
            return;
        }

        var rawX = Math.Min(_dragStart.Value.X, current.X);
        var rawY = Math.Min(_dragStart.Value.Y, current.Y);
        var (vsLeft, vsTop, _, _) = VirtualScreen.GetBounds();
        var pixelX = (int)Math.Round(rawX * dpi.DpiScaleX) + vsLeft;
        var pixelY = (int)Math.Round(rawY * dpi.DpiScaleY) + vsTop;
        var pixelW = (int)Math.Round(rawW * dpi.DpiScaleX);
        var pixelH = (int)Math.Round(rawH * dpi.DpiScaleY);

        if (pixelW < 1 || pixelH < 1)
        {
            _result = null;
        }
        else
        {
            _result = new CaptureRegion(pixelX, pixelY, pixelW, pixelH);
            PickedSnapshotBytes = TryCropSnapshot(pixelX, pixelY, pixelW, pixelH);
        }
        Close();
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
