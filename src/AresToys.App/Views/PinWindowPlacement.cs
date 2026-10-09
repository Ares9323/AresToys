using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AresToys.App.Services.Pins;

namespace AresToys.App.Views;

/// <summary>Physical-pixel placement shared by the image and video pins, used to persist and
/// restore them. Win32 coordinates are physical in this PerMonitorV2 process, so the restored
/// window lands on the same pixels whatever the scaling of the monitor it is on.</summary>
internal static partial class PinWindowPlacement
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Windows scale factors run from 100% to 500%; anything outside is a corrupt value.</summary>
    public static bool IsSaneDpiScale(double scale) => double.IsFinite(scale) && scale >= 0.5 && scale <= 5.0;

    /// <summary>Outer window rectangle in physical pixels, or null without an HWND.</summary>
    public static PixelRect? GetPhysicalBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Move the window's top-left to a physical pixel, keeping its size. Crossing onto a
    /// monitor with another DPI makes Windows send WM_DPICHANGED, and WPF answers by resizing the
    /// window to the suggested rectangle, which can shift it: a second pass corrects that.</summary>
    public static void MoveToPhysical(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        for (var pass = 0; pass < 2; pass++)
        {
            if (GetWindowRect(hwnd, out var r) && r.Left == x && r.Top == y) return;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    /// <summary>Make a layered window ignore the mouse (every click, drag and wheel goes to what
    /// is underneath) or take it back. No-op without an HWND: callers re-apply on SourceInitialized.</summary>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        var next = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        if (next != style) _ = SetWindowLongW(hwnd, GWL_EXSTYLE, next);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
