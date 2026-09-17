using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AresToys.App.Services;
using AresToys.Capture;

namespace AresToys.App.Views;

/// <summary>Full-screen transparent overlay that shows a round magnifier of the area under
/// the cursor (ShareX-style: Ellipse fed by an ImageBrush of the zoomed source crop, with a
/// red crosshair marking the centre pixel and marching-ants edge guides extending from the
/// cursor to the screen bounds). Click samples the centre pixel; Esc / right-click cancels.
/// Returns the picked hex via <see cref="PickedHex"/> after <see cref="ShowDialog"/> returns
/// true. Wheel adjusts the sample size — same control feel as the region capture overlay so
/// the two surfaces are interchangeable in the user's muscle memory.</summary>
public partial class ScreenColorPickerOverlay : Window
{
    private const int MagnifierBoxPx = 140;       // round display diameter in DIPs
    private const int MagnifierCursorOffset = 24;
    private int _magnifierHalf = 5;               // 11×11 sample default; wheel adjusts
    private int _lastSampledX = int.MinValue, _lastSampledY = int.MinValue;
    private int _lastSampledHalf = int.MinValue;  // also key the cache on zoom level so wheel forces a refresh
    private DispatcherTimer? _tickTimer;
    private System.Windows.Point _lastCursorInWindow;
    private bool _haveCursor;
    /// <summary>Side of the cursor the magnifier block currently sits on, per axis. Carried
    /// across frames so the placement is sticky: it only moves to the other side when the
    /// current one stops fitting on the monitor.</summary>
    private bool _magnifierFlippedX, _magnifierFlippedY;
    /// <summary>Frozen virtual-screen capture taken ONCE in the constructor, before the
    /// overlay paints. Sampling from this in-memory bitmap (via <see cref="CroppedBitmap"/>)
    /// instead of doing a fresh <c>CopyFromScreen</c> per frame is what eliminates two
    /// problems: (1) the picker no longer captures its own UI / cursor sprite drawn on the
    /// live desktop, and (2) per-frame work drops from "GDI roundtrip + new BitmapSource"
    /// to "create a CroppedBitmap view" — the latter is essentially free.</summary>
    private BitmapSource? _screenSnapshot;
    private int _screenSnapshotLeft;
    private int _screenSnapshotTop;

    public ScreenColorPickerOverlay()
    {
        // Capture the virtual screen BEFORE InitializeComponent + first paint. The window
        // hasn't been shown yet so the overlay's own UI can't pollute the snapshot. Reusing
        // the region overlay's helper keeps the GDI / DPI handling identical across the two
        // surfaces.
        var (snap, snapLeft, snapTop) = RegionOverlayWindow.CaptureVirtualScreen();
        _screenSnapshot = snap;
        _screenSnapshotLeft = snapLeft;
        _screenSnapshotTop = snapTop;

        InitializeComponent();
        // Wire the snapshot to the background Image BEFORE the window paints. Doing this
        // in Loaded instead leaves one frame where the window is shown empty over a black
        // (or system-default) background — on multi-monitor setups that flashes white-ish
        // for a frame and is genuinely blinding.
        if (_screenSnapshot is not null) ScreenshotImage.Source = _screenSnapshot;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        // Force keyboard focus aggressively — Esc must always close the overlay even when focus
        // policy gets weird with topmost transparent windows.
        Loaded += (_, _) =>
        {
            Activate(); Focus(); Keyboard.Focus(this);
            // Single 60 Hz tick that owns BOTH the marching-ants animation AND the magnifier
            // sample/render. Reads the cursor position from Win32 directly so wheel-only
            // gestures (no mouse movement) still refresh the picker on the next frame.
            _tickTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _tickTimer.Tick += (_, _) => Tick();
            _tickTimer.Start();
        };
        Closed += (_, _) => _tickTimer?.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; } };
    }

    /// <summary>Set when ShowDialog returns true. Format: "#RRGGBB".</summary>
    public string? PickedHex { get; private set; }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Cache cursor in window-space so the tick can position the magnifier correctly
        // even on wheel-only gestures (no mouse movement). The magnifier sample / render
        // itself happens in the 60 Hz tick to avoid GDI-per-event backpressure.
        var p = e.GetPosition(this);
        _lastCursorInWindow = p;
        _haveCursor = true;
    }

    private void Tick()
    {
        if (!_haveCursor) return;
        if (!GetCursorPos(out var native)) return;
        // Skip resampling when neither cursor nor zoom changed — the picker is already
        // showing exactly what we'd render and we'd churn the render pipeline for nothing.
        if (native.X == _lastSampledX && native.Y == _lastSampledY && _magnifierHalf == _lastSampledHalf) return;
        UpdateMagnifier(native.X, native.Y, _lastCursorInWindow);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var native)) { Close(); return; }
        var color = SamplePixelFromSnapshot(native.X, native.Y);
        if (color is null) { Close(); return; }
        PickedHex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
        DialogResult = true;
        Close();
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }

    /// <summary>Wheel adjusts the sample window size. Smaller sample = bigger pixels in the
    /// circle (more "zoom"). Range matches the region overlay so the two feel the same.
    /// The tick re-keys on _magnifierHalf so the new zoom is rendered on the next frame
    /// without requiring the user to nudge the mouse.</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _magnifierHalf = Math.Clamp(_magnifierHalf + (e.Delta > 0 ? -1 : 1), 3, 20);
        ZoomLabel.Text = $"Zoom: {_magnifierHalf * 2 + 1}px";
        e.Handled = true;
    }

    private void UpdateMagnifier(int screenX, int screenY, System.Windows.Point cursorInWindow)
    {
        var snap = _screenSnapshot;
        if (snap is null) return;
        var sampleSize = _magnifierHalf * 2 + 1;
        // Crop relative to the snapshot's origin (= virtual-screen left/top in physical
        // pixels). Clamp so the cursor near the edge still produces a valid sample rect.
        var rx = screenX - _magnifierHalf - _screenSnapshotLeft;
        var ry = screenY - _magnifierHalf - _screenSnapshotTop;
        if (rx < 0) rx = 0;
        if (ry < 0) ry = 0;
        if (rx + sampleSize > snap.PixelWidth)  rx = snap.PixelWidth  - sampleSize;
        if (ry + sampleSize > snap.PixelHeight) ry = snap.PixelHeight - sampleSize;
        if (rx >= 0 && ry >= 0 && sampleSize > 0)
        {
            // CroppedBitmap is a view into the snapshot — no copy. Setting it on an Image
            // element (vs an ImageBrush) is what makes RenderOptions.BitmapScalingMode=
            // NearestNeighbor actually take effect — on ImageBrush WPF tends to fall back
            // to bilinear and the magnified pixels come out blurred.
            var crop = new CroppedBitmap(snap, new Int32Rect(rx, ry, sampleSize, sampleSize));
            MagnifierImage.Source = crop;
        }

        // Read the centre pixel for the hex / RGB readout + show the cursor's screen
        // coordinates so the user can pick by reference (e.g. align with a known UI mark).
        if (SamplePixelFromSnapshot(screenX, screenY) is { } px)
        {
            HexLabel.Text = $"#{px.R:X2}{px.G:X2}{px.B:X2}";
            RgbLabel.Text = $"{px.R}, {px.G}, {px.B}";
        }
        CoordsLabel.Text = $"X: {screenX}  Y: {screenY}";

        // Centre crosshair is a 1-source-pixel × zoomFactor square — gives the user an
        // outline of exactly the pixel that would be sampled on click.
        var pixSize = (double)MagnifierBoxPx / sampleSize;
        MagnifierCrosshair.Width = pixSize;
        MagnifierCrosshair.Height = pixSize;

        PositionMagnifierNearCursor(cursorInWindow, screenX, screenY);
        _lastSampledX = screenX;
        _lastSampledY = screenY;
        _lastSampledHalf = _magnifierHalf;
    }

    /// <summary>Place the magnifier circle + its label card near the cursor, flipping and clamping
    /// against the CURRENT MONITOR's work area. The overlay window spans the whole virtual screen,
    /// so testing against the window's own bounds (the old behaviour) never triggered a flip at
    /// the bottom / right edge of any monitor except the last one, and the label card — whose
    /// height wasn't counted at all — got drawn past the screen edge.</summary>
    private void PositionMagnifierNearCursor(System.Windows.Point cursor, int screenX, int screenY)
    {
        var w = MagnifierGroup.ActualWidth > 0 ? MagnifierGroup.ActualWidth : MagnifierBoxPx;
        var h = MagnifierGroup.ActualHeight > 0 ? MagnifierGroup.ActualHeight : MagnifierBoxPx;
        var labelW = MagnifierLabelsBorder.ActualWidth > 0 ? MagnifierLabelsBorder.ActualWidth : 130;
        var labelH = MagnifierLabelsBorder.ActualHeight > 0 ? MagnifierLabelsBorder.ActualHeight : 92;

        // Feed the previous frame's sides back in: the block then stays put instead of hopping
        // back across the cursor the moment there's room again (issue #11 follow-up).
        var placement = MagnifierPlacement.Compute(
            cursor.X, cursor.Y,
            w, h, labelW, labelH,
            CurrentMonitorBoundsInWindow(screenX, screenY),
            wasFlippedX: _magnifierFlippedX,
            wasFlippedY: _magnifierFlippedY,
            cursorOffset: MagnifierCursorOffset);
        _magnifierFlippedX = placement.FlippedX;
        _magnifierFlippedY = placement.FlippedY;

        Canvas.SetLeft(MagnifierGroup, placement.CircleX);
        Canvas.SetTop(MagnifierGroup, placement.CircleY);
        Canvas.SetLeft(MagnifierLabelsBorder, placement.LabelsX);
        Canvas.SetTop(MagnifierLabelsBorder, placement.LabelsY);
    }

    /// <summary>Work area of the monitor under the cursor, expressed in the overlay's own
    /// coordinate space (DIPs relative to the window's top-left). The device→DIP scale is derived
    /// from the snapshot (whose pixel width covers exactly the virtual screen the window is sized
    /// to) rather than from a DPI query, so it stays consistent with the coordinates WPF hands us
    /// for the cursor. Falls back to the full window rect if the monitor query fails.</summary>
    private MagnifierPlacement.Rect CurrentMonitorBoundsInWindow(int screenX, int screenY)
    {
        var fallback = new MagnifierPlacement.Rect(0, 0, ActualWidth, ActualHeight);
        var snap = _screenSnapshot;
        if (snap is null || ActualWidth <= 0) return fallback;

        var monitor = MonitorFromPoint(new POINT { X = screenX, Y = screenY }, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return fallback;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return fallback;

        // Device pixels per DIP. snap.PixelWidth is the virtual screen in physical pixels and the
        // window's ActualWidth is that same span in DIPs.
        var scale = snap.PixelWidth / ActualWidth;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return fallback;

        // Work area (rcWork), not the full monitor rect: keeps the block clear of the taskbar,
        // which is where the report's "cropped at the bottom" case actually lands.
        var left   = (info.rcWork.Left   - _screenSnapshotLeft) / scale;
        var top    = (info.rcWork.Top    - _screenSnapshotTop)  / scale;
        var right  = (info.rcWork.Right  - _screenSnapshotLeft) / scale;
        var bottom = (info.rcWork.Bottom - _screenSnapshotTop)  / scale;
        if (right <= left || bottom <= top) return fallback;
        return new MagnifierPlacement.Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Read the colour of a single pixel out of the frozen snapshot. CopyPixels on
    /// a 1×1 CroppedBitmap returns BGRA bytes — we map back to RGB. Returns null when the
    /// requested coords fall outside the snapshot bounds (rare — virtual screen edge).</summary>
    private (byte R, byte G, byte B)? SamplePixelFromSnapshot(int physicalX, int physicalY)
    {
        var snap = _screenSnapshot;
        if (snap is null) return null;
        var sx = physicalX - _screenSnapshotLeft;
        var sy = physicalY - _screenSnapshotTop;
        if (sx < 0 || sy < 0 || sx >= snap.PixelWidth || sy >= snap.PixelHeight) return null;
        try
        {
            var crop = new CroppedBitmap(snap, new Int32Rect(sx, sy, 1, 1));
            var bgra = new byte[4];
            crop.CopyPixels(bgra, 4, 0);
            return (bgra[2], bgra[1], bgra[0]);
        }
        catch (ArgumentException) { return null; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
