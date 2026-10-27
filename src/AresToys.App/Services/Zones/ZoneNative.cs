using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AresToys.App.Services.Zones;

/// <summary>Win32 imports and constants used by the zone drag service, the overlay and the
/// monitor helper. All coordinates are device pixels (the app is PerMonitorV2 aware).</summary>
internal static partial class ZoneNative
{
    public const uint EventSystemMoveSizeStart = 0x000A;
    public const uint EventSystemMoveSizeEnd = 0x000B;
    public const uint EventObjectShow = 0x8002;
    public const uint EventObjectUncloaked = 0x8018;
    public const int ObjidWindow = 0;
    public const int ChildIdSelf = 0;

    public const int VkLButton = 0x01;
    public const int VkRButton = 0x02;
    public const int VkShift = 0x10;
    public const int VkEscape = 0x1B;
    public const int SmSwapButton = 23;

    public const int VkControl = 0x11;

    // System sizing cursors (MAKEINTRESOURCE ids): north south, west east, NE to SW, NW to SE.
    public const int IdcSizeNwse = 32642;
    public const int IdcSizeNesw = 32643;
    public const int IdcSizeWe = 32644;
    public const int IdcSizeNs = 32645;

    public const int SwHide = 0;
    public const int SwRestore = 9;
    public const int SwShowNoActivate = 4;

    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const nint WsChild = 0x40000000;
    public const nint WsCaption = 0x00C00000;
    public const nint WsExTransparent = 0x00000020;
    public const nint WsExToolWindow = 0x00000080;
    public const nint WsExNoActivate = 0x08000000;

    public static readonly nint HwndTopmost = new(-1);

    public const uint MonitorDefaultToNearest = 2;
    public const int MdtEffectiveDpi = 0;
    public const int DwmaExtendedFrameBounds = 9;
    public const int DwmaWindowCornerPreference = 33;
    public const int DwmwcpDefault = 0;
    public const int DwmwcpDoNotRound = 1;

    public const uint GwOwner = 4;
    public const uint GwChild = 5;
    public const uint GwHwndNext = 2;
    public const uint GaRoot = 2;
    public const int DwmaCloaked = 14;
    public const uint LwaAlpha = 0x2;
    public const nint WsExLayered = 0x00080000;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    // Mouse buttons polled during a drag (see ZoneDragService per drag toggles).
    public const int VkDigit0 = 0x30;
    public const int VkNumpad0 = 0x60;
    public const int VkMButton = 0x04;
    public const int VkXButton1 = 0x05;
    public const int VkXButton2 = 0x06;

    // Keyboard snapping (see ZoneKeyboardSnapper): the arrow keys, and the foreground change event it caches the
    // eligibility of the foreground window on.
    public const uint EventSystemForeground = 0x0003;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    /// <summary>The 32 UTF-16 code units of szDevice. Held as ushort so the struct stays blittable.</summary>
    [InlineArray(32)]
    public struct DeviceName
    {
        private ushort _first;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public DeviceName Device;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    public delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public Point ScreenPos;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint hInstance, nint cursorName);

    /// <summary>True when the current mouse cursor is one of the four sizing cursors, which means the
    /// move/size loop that just started is a resize. Same test as PowerToys IsCursorTypeIndicatingSizeEvent.</summary>
    public static bool IsSizingCursor()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Cursor == 0) return false;

        return info.Cursor == LoadCursor(0, IdcSizeNs)
            || info.Cursor == LoadCursor(0, IdcSizeWe)
            || info.Cursor == LoadCursor(0, IdcSizeNesw)
            || info.Cursor == LoadCursor(0, IdcSizeNwse);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>Moves a topmost, non activating window onto <paramref name="area"/> in device pixels, showing it
    /// when <paramref name="show"/> is set. Crossing to a monitor with another DPI makes WPF answer WM_DPICHANGED by
    /// resizing the window to Windows' suggestion, so the rectangle is checked and applied again until it holds.</summary>
    public static void PlaceTopmost(nint hwnd, PixelRect area, bool show)
    {
        var flags = SwpNoActivate | (show ? SwpShowWindow : 0u);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            SetWindowPos(hwnd, HwndTopmost, area.Left, area.Top, area.Width, area.Height, flags);
            if (GetWindowRect(hwnd, out var actual)
                && actual.Left == area.Left && actual.Top == area.Top
                && actual.Right == area.Right && actual.Bottom == area.Bottom)
            {
                break;
            }
        }
    }

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttributeInt(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLayeredWindowAttributes(nint hWnd, out uint colorKey, out byte alpha, out uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hWnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] exeName, ref uint size);

    /// <summary>True when the window has an owner that is visible and not zero sized: a dialog or tool window of
    /// another window. Same test as PowerToys FancyZonesWindowUtils::HasVisibleOwner, which its "allow snapping
    /// child windows" setting switches on (off rejects such windows).</summary>
    public static bool HasVisibleOwner(nint hwnd)
    {
        var owner = GetWindow(hwnd, GwOwner);
        if (owner == 0 || !IsWindowVisible(owner)) return false;
        if (!GetWindowRect(owner, out var rect)) return true;
        return rect.Top != rect.Bottom && rect.Left != rect.Right;
    }

    /// <summary>The full path of the exe of the process that owns the window, or null when it cannot be opened or
    /// read (an elevated process, for example).</summary>
    public static string? GetProcessPath(nint hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0) return null;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The exe file name of the process that owns the window (for example <c>notepad.exe</c>), or null
    /// when it cannot be opened or read.</summary>
    public static string? GetProcessFileName(nint hwnd)
        => GetProcessPath(hwnd) is { } path ? System.IO.Path.GetFileName(path) : null;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>True for the desktop and the taskbars, which are never snapped.</summary>
    public static bool IsShellWindow(nint hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 && ShellClasses.Contains(new string(buffer, 0, length));
    }

    /// <summary>The one eligibility test for a window the snapping may place: it exists, is visible and not
    /// minimized, is not a child window or a shell window, passes the "allow snapping child windows" option
    /// (a window with a visible owner) and is not in the excluded apps list. The drag service adds its own
    /// checks (our overlays, a move and not a resize) and the window tracker adds the ones that fit a window that
    /// has just appeared (a title bar, not a tool window, not one of ours).</summary>
    public static bool IsSnappable(nint hwnd, WindowSnappingSettings options)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if ((GetWindowLongPtr(hwnd, GwlStyle) & WsChild) != 0) return false;
        if (IsShellWindow(hwnd)) return false;

        // A window with a visible owner (a dialog or tool window of another window) is PowerToys' "child window".
        if (!options.AllowChildWindows && HasVisibleOwner(hwnd)) return false;

        // The process lookup is the costly part, so it comes last and only when there is a list to check.
        return options.ExcludedApps.Count == 0 || !options.IsExcluded(GetProcessFileName(hwnd));
    }
}
