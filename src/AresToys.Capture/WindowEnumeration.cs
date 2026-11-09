using System.Drawing;
using System.Runtime.InteropServices;

namespace AresToys.Capture;

public sealed record WindowSnapshot(string Title, int X, int Y, int Width, int Height);

/// <summary>
/// Window + child-control enumeration used by the region picker, ported 1:1 from ShareX's
/// <c>WindowsRectangleList</c> (ShareX.ScreenCaptureLib). The semantics:
///   - top-level windows enumerated z-order topmost first;
///   - for each top-level we recurse into its descendants (every child control), clipped to
///     the top-level's rect — these come BEFORE the parent in the list so the picker's
///     <c>FirstOrDefault</c> hits the most specific child first;
///   - for each top-level we also add its client rect (the inner area without title bar /
///     borders) when it's different from the window rect — same as ShareX;
///   - top-level windows use DWM's <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> (when available) instead
///     of <c>GetWindowRect</c> so the resizing border on Win10/11 is excluded;
///   - de-dup pass: a child rect that's fully contained by an already-added result entry is
///     skipped (avoids inner controls of a window behind the topmost cluttering the list).
/// </summary>
public static class WindowEnumeration
{
    /// <summary>Internal record carrying the IsWindow flag we need for the ShareX-style dedup.
    /// Kept private so the public surface stays a flat list of <see cref="WindowSnapshot"/>.</summary>
    private sealed record WindowEntry(IntPtr Handle, Rectangle Rectangle, bool IsWindow, string Title);

    public static IReadOnlyList<WindowSnapshot> EnumerateVisibleWindows()
    {
        var raw = new List<WindowEntry>();
        var visited = new HashSet<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            try { CheckHandle(hWnd, parentClip: null, parentTitle: null, raw, visited); }
            catch { /* one bad window doesn't kill the rest */ }
            return true;
        }, IntPtr.Zero);

        // Dedup pass — same as ShareX: skip child rects that are fully contained by an entry
        // already in the result. Keeps top-levels always (IsWindow), drops redundant inner
        // children of windows behind the topmost.
        var result = new List<WindowEntry>(raw.Count);
        foreach (var w in raw)
        {
            var visible = true;
            if (!w.IsWindow)
            {
                foreach (var seen in result)
                {
                    if (seen.Rectangle.Contains(w.Rectangle))
                    {
                        visible = false;
                        break;
                    }
                }
            }
            if (visible) result.Add(w);
        }

        return result.Select(e => new WindowSnapshot(
            e.Title,
            e.Rectangle.X,
            e.Rectangle.Y,
            e.Rectangle.Width,
            e.Rectangle.Height)).ToList();
    }

    private static void CheckHandle(IntPtr handle, Rectangle? parentClip, string? parentTitle, List<WindowEntry> windows, HashSet<IntPtr> visited)
    {
        var isWindow = parentClip is null;

        if (!IsWindowVisible(handle)) return;
        if (isWindow && IsWindowCloaked(handle)) return;
        if (isWindow && IsToolNoActivate(handle)) return;

        // Resolve the rect: top-levels go through DWM extended-frame-bounds (excludes the
        // invisible resize border on Win10/11); children just use GetWindowRect clipped to
        // parent. ShareX doesn't filter by title — empty-titled top-levels still get added so
        // we match. The IsWindowVisible / cloaked / tool filters above are enough to keep the
        // list clean.
        Rectangle rect;
        string title;
        if (isWindow)
        {
            rect = GetWindowRectangle(handle);
            title = GetWindowTitle(handle);
        }
        else
        {
            if (!GetWindowRectRaw(handle, out var r)) return;
            rect = Rectangle.Intersect(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), parentClip!.Value);
            title = parentTitle ?? string.Empty;
        }
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Recurse into children FIRST so they appear before the parent in the final list →
        // FirstOrDefault picks the deepest matching child before falling back to the parent.
        if (visited.Add(handle))
        {
            EnumChildWindows(handle, (childHwnd, _) =>
            {
                try { CheckHandle(childHwnd, rect, title, windows, visited); }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        // For top-levels also add the client rect (inside title bar / borders) when it differs
        // from the window rect — gives users a "client-area only" snap target the same way
        // ShareX does.
        if (isWindow)
        {
            var clientRect = GetClientAreaRect(handle);
            if (clientRect.Width > 0 && clientRect.Height > 0 && clientRect != rect)
                windows.Add(new WindowEntry(handle, clientRect, IsWindow: false, title));
        }

        windows.Add(new WindowEntry(handle, rect, isWindow, title));
    }

    /// <summary>Snapshot of the current foreground window (DWM extended-frame-bounds aware, so the
    /// invisible Win10/11 resize border is excluded). Returns <c>null</c> when there's no foreground
    /// window, when it's cloaked, or when its rect collapses to nothing (e.g. minimised). Also
    /// rejects any window owned by <paramref name="excludeProcessId"/> so the caller can ignore
    /// itself when the entry point is a tray menu (the menu briefly owns the foreground).</summary>
    public static WindowSnapshot? GetForegroundWindowSnapshot(int? excludeProcessId = null)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        if (excludeProcessId is int pidToSkip)
        {
            // Threadid return value isn't useful here — we only care about pid; suppress CA1806
            // by discarding it explicitly.
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid == pidToSkip) return null;
        }

        if (!IsWindowVisible(hwnd)) return null;
        if (IsWindowCloaked(hwnd)) return null;

        var rect = GetWindowRectangle(hwnd);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        var title = GetWindowTitle(hwnd);
        return new WindowSnapshot(title, rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>Find the visible window/child rect at the given screen-pixel point. FirstOrDefault
    /// matching honours the build order: deepest children first within the topmost window, then
    /// down through z-order — so a child of an occluded window never beats the foreground app.</summary>
    public static WindowSnapshot? FindWindowAt(int x, int y, IReadOnlyList<WindowSnapshot> windows)
    {
        foreach (var w in windows)
        {
            if (x >= w.X && x < w.X + w.Width && y >= w.Y && y < w.Y + w.Height) return w;
        }
        return null;
    }

    /// <summary>Process name (e.g. <c>firefox</c>) of the top-level window at a screen-pixel
    /// point. Null when there's no window, it belongs to <paramref name="excludeProcessId"/>
    /// (our own overlays / pins), or the process can't be queried (elevated, already gone).
    /// Feeds the <c>%appName</c> file-name token for region captures.</summary>
    public static string? GetProcessNameAt(int x, int y, int? excludeProcessId = null)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return null;
        var root = GetAncestor(hwnd, GA_ROOT);
        return ProcessNameOf(root == IntPtr.Zero ? hwnd : root, excludeProcessId);
    }

    /// <summary>Process name of the current foreground window, same exclusions as
    /// <see cref="GetProcessNameAt"/>. Used for whole-window / monitor captures.</summary>
    public static string? GetForegroundProcessName(int? excludeProcessId = null)
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : ProcessNameOf(hwnd, excludeProcessId);
    }

    private static string? ProcessNameOf(IntPtr hwnd, int? excludeProcessId)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || (excludeProcessId is int skip && (int)pid == skip)) return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return null; }
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    // ── Win32 helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Same as ShareX's <c>CaptureHelpers.GetWindowRectangle</c>: prefer DWM extended
    /// frame bounds (excludes the invisible resize border on Win10/11); fall back to GetWindowRect.</summary>
    private static Rectangle GetWindowRectangle(IntPtr handle)
    {
        if (IsDwmEnabled() && GetExtendedFrameBounds(handle, out var dwmRect))
            return dwmRect;
        if (GetWindowRectRaw(handle, out var r))
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return Rectangle.Empty;
    }

    private static bool GetExtendedFrameBounds(IntPtr handle, out Rectangle rect)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS = 9
        var hr = DwmGetWindowAttribute(handle, 9, out RECT raw, Marshal.SizeOf<RECT>());
        if (hr == 0)
        {
            rect = new Rectangle(raw.Left, raw.Top, raw.Right - raw.Left, raw.Bottom - raw.Top);
            return true;
        }
        rect = Rectangle.Empty;
        return false;
    }

    private static Rectangle GetClientAreaRect(IntPtr handle)
    {
        if (!GetClientRect(handle, out var r)) return Rectangle.Empty;
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(handle, ref pt);
        return new Rectangle(pt.X, pt.Y, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static bool GetWindowRectRaw(IntPtr handle, out RECT rect) => GetWindowRectNative(handle, out rect);

    private static bool IsDwmEnabled()
    {
        var hr = DwmIsCompositionEnabled(out var enabled);
        return hr == 0 && enabled;
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        // DWMWA_CLOAKED = 14
        if (!IsDwmEnabled()) return false;
        if (DwmGetWindowAttribute(hWnd, 14, out int cloaked, sizeof(int)) == 0)
            return cloaked != 0;
        return false;
    }

    private static bool IsToolNoActivate(IntPtr hWnd)
    {
        const long WS_EX_TOOLWINDOW = 0x00000080L;
        const long WS_EX_NOACTIVATE = 0x08000000L;
        var ex = GetWindowLongPtr(hWnd, -20).ToInt64(); // GWL_EXSTYLE
        return (ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_NOACTIVATE) != 0;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buf = new char[len + 1];
        int copied;
        unsafe
        {
            fixed (char* p = buf) { copied = GetWindowText(hWnd, p, buf.Length); }
        }
        return copied <= 0 ? string.Empty : new string(buf, 0, copied);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRectNative(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern unsafe int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern int DwmIsCompositionEnabled(out bool pfEnabled);
}
