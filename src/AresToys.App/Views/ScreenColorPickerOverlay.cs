using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AresToys.App.Services;

namespace AresToys.App.Views;

/// <summary>The screen colour picker session: a round magnifier of the area under the cursor
/// (ShareX-style lens plus a hex / RGB / coordinates label box) drawn over a frozen snapshot of
/// the desktop. Click samples the pixel under the cursor; Esc / right-click cancels; the wheel
/// adjusts the sample size, the same control feel as the region capture overlay.
/// <para>The session owns all picker state in physical pixels. The windows are
/// <see cref="ScreenColorPickerView"/>s, one per monitor, placed and closed by
/// <see cref="PerMonitorOverlayHost"/>; they only draw and forward input here.</para></summary>
public class ScreenColorPickerOverlay
{
    private const int MagnifierBoxPx = 140;       // round display diameter in DIPs
    private const int MagnifierCursorOffset = 24;

    private readonly PerMonitorOverlayHost _host = new();
    private readonly List<ScreenColorPickerView> _views = new();
    private IReadOnlyList<OverlayMonitor> _monitors = Array.Empty<OverlayMonitor>();
    private BitmapSource? _snapshot;
    private int _snapshotLeft, _snapshotTop;
    private int _half = 5;                        // 11x11 sample default; wheel adjusts
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private int _lastHalf = int.MinValue;         // also keys the cache on zoom so the wheel forces a refresh
    /// <summary>Side of the cursor the lens block currently sits on, per axis. Carried across
    /// frames so the placement is sticky: it only moves to the other side when the current one
    /// stops fitting on the monitor.</summary>
    private bool _flippedX, _flippedY;
    private DispatcherTimer? _timer;
    private string? _result;
    private bool _ended;

    /// <summary>Opens the picker and waits for a click or a cancel. Returns "#RRGGBB", or null on
    /// cancel (or when the screen could not be captured). Call it on the UI thread.</summary>
    public string? Pick()
    {
        // The snapshot is taken before any view exists, so the overlay's own UI can't pollute it.
        var (snapshot, left, top) = PerMonitorOverlayHost.CaptureVirtualScreen();
        if (snapshot is null) return null;
        _snapshot = snapshot;
        _snapshotLeft = left;
        _snapshotTop = top;

        var monitors = PerMonitorOverlayHost.EnumerateMonitors();
        if (monitors.Count == 0) return null;
        _monitors = monitors;

        var views = new List<(Window View, OverlayMonitor Monitor)>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var view = new ScreenColorPickerView(
                this, monitor, PerMonitorOverlayHost.CropToMonitor(snapshot, left, top, monitor));
            _views.Add(view);
            views.Add((view, monitor));
        }

        // Single 60 Hz tick that owns the lens sample / render. It reads the cursor from Win32
        // directly so wheel-only gestures (no mouse movement) still refresh the picker on the next
        // frame. Queued before Run: it is started from inside the host's frame, once every view
        // has been shown.
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => Tick();
        _timer = timer;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_ended) timer.Start();
        });
        try
        {
            _host.Run(views);
        }
        finally
        {
            timer.Stop();
            _ended = true;
        }
        return _result;
    }

    /// <summary>Left button: sample the snapshot pixel under the cursor and end with its hex.
    /// A pixel outside the snapshot ends the pick without a result.</summary>
    internal void Commit(int screenX, int screenY)
    {
        var color = SamplePixel(screenX, screenY);
        End(color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null);
    }

    /// <summary>Right button or Esc: end without a result.</summary>
    internal void Cancel() => End(null);

    /// <summary>Wheel: smaller sample = bigger pixels in the circle (more "zoom"). The tick re-keys
    /// on the zoom, so the new level is drawn on the next frame without a mouse movement.</summary>
    internal void Zoom(int wheelDelta) => _half = Math.Clamp(_half + (wheelDelta > 0 ? -1 : 1), 3, 20);

    private void End(string? result)
    {
        if (_ended) return;
        _ended = true;
        _result = result;
        _timer?.Stop();
        _host.End();
    }

    private void Tick()
    {
        if (_ended) return;
        if (!GetCursorPos(out var cursor)) return;
        // Skip resampling when neither cursor nor zoom changed: the picker already shows exactly
        // what we'd render and we'd churn the render pipeline for nothing.
        if (cursor.X == _lastX && cursor.Y == _lastY && _half == _lastHalf) return;
        Update(cursor.X, cursor.Y);
    }

    private void Update(int screenX, int screenY)
    {
        var snap = _snapshot;
        if (snap is null) return;
        var target = ViewFor(screenX, screenY);
        if (target is null) return;

        var sampleSize = _half * 2 + 1;
        // Crop relative to the snapshot's origin (= virtual-screen left/top in physical pixels).
        // Clamp so the cursor near the edge still produces a valid sample rect.
        var rx = screenX - _half - _snapshotLeft;
        var ry = screenY - _half - _snapshotTop;
        if (rx < 0) rx = 0;
        if (ry < 0) ry = 0;
        if (rx + sampleSize > snap.PixelWidth) rx = snap.PixelWidth - sampleSize;
        if (ry + sampleSize > snap.PixelHeight) ry = snap.PixelHeight - sampleSize;
        // CroppedBitmap is a view into the snapshot (no copy). Set on an Image element (not an
        // ImageBrush) so BitmapScalingMode=NearestNeighbor takes effect.
        BitmapSource? lens = rx >= 0 && ry >= 0 && sampleSize > 0
            ? new CroppedBitmap(snap, new Int32Rect(rx, ry, sampleSize, sampleSize))
            : null;

        string? hex = null, rgb = null;
        if (SamplePixel(screenX, screenY) is { } px)
        {
            hex = $"#{px.R:X2}{px.G:X2}{px.B:X2}";
            rgb = $"{px.R}, {px.G}, {px.B}";
        }

        // Place the lens block inside THIS monitor's work area, in this view's DIPs.
        var (circleW, circleH, labelsW, labelsH) = target.MagnifierSizes();
        var cursorInView = target.ToDips(screenX, screenY);
        var placement = MagnifierPlacement.Compute(
            cursorInView.X, cursorInView.Y,
            circleW, circleH, labelsW, labelsH,
            target.WorkAreaInDips(),
            wasFlippedX: _flippedX,
            wasFlippedY: _flippedY,
            cursorOffset: MagnifierCursorOffset);
        _flippedX = placement.FlippedX;
        _flippedY = placement.FlippedY;

        foreach (var view in _views)
        {
            if (view != target) view.HideMagnifier();
        }
        target.ShowMagnifier(new MagnifierFrame(
            lens, hex, rgb,
            Coords: $"X: {screenX}  Y: {screenY}",
            Zoom: $"Zoom: {sampleSize}px",
            // The centre crosshair is a 1-source-pixel x zoom-factor square: an outline of exactly
            // the pixel that would be sampled on click.
            CrosshairSize: (double)MagnifierBoxPx / sampleSize,
            placement));

        _lastX = screenX;
        _lastY = screenY;
        _lastHalf = _half;
    }

    /// <summary>The view on the monitor that holds the point, else on the nearest one.</summary>
    private ScreenColorPickerView? ViewFor(int screenX, int screenY)
    {
        var index = PerMonitorOverlayHost.NearestMonitor(_monitors, screenX, screenY);
        return index < 0 ? null : _views[index];
    }

    /// <summary>Read the colour of a single pixel out of the frozen snapshot. CopyPixels on a 1x1
    /// CroppedBitmap returns BGRA bytes, mapped back to RGB. Returns null when the coordinates
    /// fall outside the snapshot.</summary>
    private (byte R, byte G, byte B)? SamplePixel(int physicalX, int physicalY)
    {
        var snap = _snapshot;
        if (snap is null) return null;
        var sx = physicalX - _snapshotLeft;
        var sy = physicalY - _snapshotTop;
        if (sx < 0 || sy < 0 || sx >= snap.PixelWidth || sy >= snap.PixelHeight) return null;
        try
        {
            var crop = new CroppedBitmap(snap, new Int32Rect(sx, sy, 1, 1));
            var bgra = new byte[4];
            crop.CopyPixels(bgra, 4, 0);
            return (bgra[2], bgra[1], bgra[0]);
        }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
