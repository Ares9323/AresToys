using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AresToys.Capture;

namespace AresToys.App.Views;

/// <summary>One monitor in physical virtual-screen pixels: <see cref="Bounds"/> is rcMonitor,
/// <see cref="WorkArea"/> is rcWork.</summary>
internal readonly record struct OverlayMonitor(Int32Rect Bounds, Int32Rect WorkArea, bool IsPrimary);

/// <summary>The one owner of "an overlay on every monitor". WPF hardware rendering loses about
/// 288 MB of video memory on every Show of a window that spans more than one monitor (run 2294,
/// finding 2365), and software rendering is not usable for an interactive overlay (finding 2385).
/// The fix is one hardware-rendered window per monitor, each placed exactly on its own monitor,
/// so no window is ever rendered while it spans two monitors. The host enumerates monitors,
/// captures the virtual screen, places and shows the views, activates the one under the cursor,
/// waits in a nested dispatcher frame and closes every view when it ends. A view never places
/// itself: it only draws and forwards input to its session.</summary>
internal sealed class PerMonitorOverlayHost
{
    private readonly DispatcherFrame _frame = new();
    private readonly List<Window> _open = new();

    /// <summary>Every attached monitor, in physical virtual-screen pixels.</summary>
    public static IReadOnlyList<OverlayMonitor> EnumerateMonitors()
    {
        var monitors = new List<OverlayMonitor>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new OverlayMonitor(
                    ToInt32Rect(info.rcMonitor),
                    ToInt32Rect(info.rcWork),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors;
    }

    /// <summary>Index in <paramref name="monitors"/> of the monitor that holds the point, else of
    /// the nearest one (the virtual screen has areas that no monitor covers); -1 for an empty
    /// list. Sessions build their views in the order <see cref="EnumerateMonitors"/> returns, so
    /// the index is also the index of the view.</summary>
    public static int NearestMonitor(IReadOnlyList<OverlayMonitor> monitors, int screenX, int screenY)
    {
        var best = -1;
        long bestDistance = long.MaxValue;
        for (var i = 0; i < monitors.Count; i++)
        {
            var b = monitors[i].Bounds;
            long dx = Math.Max(Math.Max(b.X - screenX, 0), screenX - (b.X + b.Width - 1));
            long dy = Math.Max(Math.Max(b.Y - screenY, 0), screenY - (b.Y + b.Height - 1));
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance) { best = i; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Snapshot the entire virtual screen RIGHT NOW and return a frozen
    /// <see cref="BitmapSource"/> alongside its origin in physical pixels. Static / public
    /// so callers can capture at the EARLIEST point in their pipeline (before any focus
    /// shift caused by clicking a tray menu, opening a window, etc.). Returns
    /// <c>(null, left, top)</c> on Win32 failure.</summary>
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

    /// <summary>The part of <paramref name="snapshot"/> (whose top-left sits at
    /// <paramref name="snapshotLeft"/>/<paramref name="snapshotTop"/> on the virtual screen) that
    /// lies on <paramref name="monitor"/>: a frozen view into the snapshot, no pixel copy.
    /// Painted with Stretch Fill into a view placed exactly on the monitor it is one physical
    /// pixel per screen pixel.</summary>
    public static BitmapSource CropToMonitor(BitmapSource snapshot, int snapshotLeft, int snapshotTop, OverlayMonitor monitor)
    {
        var b = monitor.Bounds;
        var crop = new CroppedBitmap(snapshot, new Int32Rect(b.X - snapshotLeft, b.Y - snapshotTop, b.Width, b.Height));
        crop.Freeze();
        return crop;
    }

    /// <summary>Places and shows every view, activates the one on the monitor that holds the
    /// cursor, then waits in a nested dispatcher frame (never ShowDialog: it would disable the
    /// other views) until <see cref="End"/> or until any view closes. Returns with every view
    /// closed.</summary>
    public void Run(IReadOnlyList<(Window View, OverlayMonitor Monitor)> views)
    {
        if (views.Count == 0) return;
        try
        {
            foreach (var (view, monitor) in views)
            {
                view.WindowStartupLocation = WindowStartupLocation.Manual;
                view.ShowActivated = false;
                // The exact rect is applied before the first frame, in two moves: the first
                // lands the window on its monitor's DPI (WPF handles the DPI change), the
                // second gives it the exact physical rect.
                view.SourceInitialized += (_, _) => Place(view, monitor);
                view.Closed += (_, _) => { _open.Remove(view); End(); };
                _open.Add(view);
            }

            foreach (var (view, _) in views) view.Show();

            ActivateViewUnderCursor(views);
            Dispatcher.PushFrame(_frame);
        }
        finally
        {
            foreach (var view in _open.ToArray()) view.Close();
        }
    }

    /// <summary>Ends the wait started by <see cref="Run"/>. Safe to call twice and from inside
    /// a view's event handler: the views are closed once the frame has unwound.</summary>
    public void End() => _frame.Continue = false;

    private static void ActivateViewUnderCursor(IReadOnlyList<(Window View, OverlayMonitor Monitor)> views)
    {
        var target = views[0].View;
        if (GetCursorPos(out var cursor))
        {
            foreach (var (view, monitor) in views)
            {
                var b = monitor.Bounds;
                if (cursor.X >= b.X && cursor.X < b.X + b.Width && cursor.Y >= b.Y && cursor.Y < b.Y + b.Height)
                {
                    target = view;
                    break;
                }
            }
        }
        // Force keyboard focus aggressively: Esc must always end the overlay even when focus
        // policy gets weird with topmost windows.
        target.Activate();
        target.Focus();
        Keyboard.Focus(target);
    }

    private static void Place(Window view, OverlayMonitor monitor)
    {
        var hwnd = new WindowInteropHelper(view).Handle;
        var b = monitor.Bounds;
        SetWindowPos(hwnd, HWND_TOPMOST, b.X + 1, b.Y, b.Width - 1, b.Height, SWP_NOACTIVATE);
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
    }

    private static Int32Rect ToInt32Rect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

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

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
