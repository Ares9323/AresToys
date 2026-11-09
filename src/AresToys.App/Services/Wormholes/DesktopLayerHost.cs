using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Wormholes;

/// <summary>Parents WPF wormhole windows to Windows' desktop <c>WorkerW</c> so they live on
/// the desktop layer — above the wallpaper but below every other top-level window. Clicking on
/// any other app pushes a wormhole behind it automatically; Win+D ("Show desktop") reveals all
/// wormholes alongside the desktop icons.
///
/// The trick: <c>Progman</c> normally owns the desktop icons and the wallpaper. Sending it
/// undocumented message <c>0x052C</c> forces a sibling <c>WorkerW</c> window to spawn between
/// the wallpaper and the icons (this is the same pattern used by Wallpaper Engine, Lively
/// Wallpaper, and Stardock Fences). We then enumerate top-level windows to find the WorkerW
/// that's a sibling of the one owning <c>SHELLDLL_DefView</c>, and reparent the wormhole there.
///
/// Failure modes are intentionally gentle: any step that comes up empty logs a warning and
/// returns false, letting <see cref="WormholeWindowManager"/> fall back to <c>Topmost=True</c>
/// for that window. Windows 11 24H2+ may change the message ID; the fallback keeps the feature
/// alive while we adapt.</summary>
public sealed class DesktopLayerHost
{
    // Magic message — Progman responds by spawning a sibling WorkerW window. Undocumented but
    // stable across every Windows release since 7. The community calls it "0x052C" or
    // "WM_SPAWN_WORKER_WINDOW"; Microsoft has never officially named it.
    private const uint MagicProgmanSpawnWorkerWMessage = 0x052C;
    private const uint SendMessageTimeoutNormal = 0x0000;
    private const int GwlExStyle = -20;
    // WS_EX_NOACTIVATE: window doesn't steal focus when it's clicked into; click-to-drag still
    // works via mouse capture.
    private const int WsExNoActivate = 0x08000000;
    // WS_EX_TOOLWINDOW: keeps the window out of Alt+Tab and the taskbar (we already have
    // ShowInTaskbar=False in XAML but Alt+Tab is a separate Win32 list).
    private const int WsExToolWindow = 0x00000080;

    private readonly ILogger<DesktopLayerHost> _logger;
    private IntPtr _workerW = IntPtr.Zero;
    private bool _searched;

    public DesktopLayerHost(ILogger<DesktopLayerHost> logger) { _logger = logger; }

    /// <summary>Reparent <paramref name="window"/> to the desktop's WorkerW. Returns false if
    /// the WorkerW can't be located (e.g. unusual Windows configuration, future OS change) —
    /// the caller should set <see cref="Window.Topmost"/> on the window as a fallback so it
    /// stays usable instead of falling behind every app.</summary>
    public bool ParentToDesktop(Window window)
    {
        if (window is null) return false;
        var workerW = EnsureWorkerW();
        if (workerW == IntPtr.Zero) return false;

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        if (helper.Handle == IntPtr.Zero) return false;

        // WS_EX_NOACTIVATE keeps the wormhole from stealing focus when clicked. Without this,
        // clicking the wormhole would activate it, push the previous foreground window behind,
        // and break the "I clicked on something, now the wormhole goes behind" intuition.
        var current = GetWindowLong(helper.Handle, GwlExStyle);
        var updated = current | WsExNoActivate | WsExToolWindow;
        if (current != updated)
        {
            // Returned value is the previous style word; logged on zero for diagnostics rather
            // than treated as fatal — even if the call fails the SetParent below is what
            // actually controls Z-order, the NOACTIVATE bit is just a focus-stealing prevention.
            var prevStyle = SetWindowLong(helper.Handle, GwlExStyle, updated);
            if (prevStyle == 0) _logger.LogDebug("SetWindowLong returned 0 on handle 0x{Handle:X}", helper.Handle.ToInt64());
        }

        var prev = SetParent(helper.Handle, workerW);
        if (prev == IntPtr.Zero)
        {
            _logger.LogWarning("SetParent(WorkerW) failed for window handle 0x{Handle:X}", helper.Handle.ToInt64());
            return false;
        }
        return true;
    }

    private IntPtr EnsureWorkerW()
    {
        if (_workerW != IntPtr.Zero) return _workerW;
        // Cache the negative result too — if no strategy finds a viable parent the first time,
        // hammering Win32 on every wormhole spawn won't help.
        if (_searched) return IntPtr.Zero;
        _searched = true;

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            _logger.LogWarning("Progman window not found — desktop layer parenting unavailable");
            return IntPtr.Zero;
        }

        // Send the magic message twice with different params. The classic (IntPtr.Zero, IntPtr.Zero)
        // form works on Win7-Win10; Win11 24H2+ sometimes only responds to the (0xD, 0x1)
        // variant observed in newer wallpaper-engine code paths.
        SendMessageTimeout(progman, MagicProgmanSpawnWorkerWMessage, IntPtr.Zero, IntPtr.Zero,
            SendMessageTimeoutNormal, 1000, out _);
        SendMessageTimeout(progman, MagicProgmanSpawnWorkerWMessage, new IntPtr(0xD), new IntPtr(0x1),
            SendMessageTimeoutNormal, 1000, out _);

        // Strategy 1: classic pattern. The WorkerW we want is the one whose preceding sibling
        // (Z-order-wise) hosts SHELLDLL_DefView — that's the desktop-icons container.
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            var sibling = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            if (sibling != IntPtr.Zero) { found = sibling; return false; }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero)
        {
            _workerW = found;
            _logger.LogInformation("WorkerW resolved via SHELLDLL_DefView sibling pattern: 0x{Handle:X}",
                _workerW.ToInt64());
            return _workerW;
        }

        // Strategy 2: any WorkerW that's a direct child of Progman. Some configurations host
        // the icons-WorkerW directly under Progman without making it a top-level sibling.
        var asChild = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (asChild != IntPtr.Zero)
        {
            _workerW = asChild;
            _logger.LogInformation("WorkerW resolved as Progman child: 0x{Handle:X}", _workerW.ToInt64());
            return _workerW;
        }

        // Strategy 3: any top-level WorkerW. Win11 24H2+ has been observed leaving the WorkerW
        // floating at the top level without a Progman parent / icon-host sibling.
        var asTop = FindWindow("WorkerW", null);
        if (asTop != IntPtr.Zero)
        {
            _workerW = asTop;
            _logger.LogInformation("WorkerW resolved as top-level: 0x{Handle:X}", _workerW.ToInt64());
            return _workerW;
        }

        // Strategy 4: parent directly to Progman. Not as clean as a dedicated WorkerW (the
        // wormhole will share a parent with the desktop icons, which can affect repaint order
        // on icon refresh) but it puts the window in the desktop layer with the same Z-order
        // semantics — clicks on other apps push the wormhole behind, Win+D reveals it.
        _workerW = progman;
        _logger.LogInformation("Using Progman directly as wormhole parent (no WorkerW found): 0x{Handle:X}",
            _workerW.ToInt64());
        return _workerW;
    }

    // CharSet.Unicode + LPWStr marshaling: silences CA2101 (the analyzer wants explicit
    // marshaling on every string-bearing P/Invoke) and binds the W variants of the user32
    // functions, which is what Windows uses internally anyway.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
        [MarshalAs(UnmanagedType.LPWStr)] string lpszClass,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszWindow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
