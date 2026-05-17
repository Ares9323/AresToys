using System.Runtime.InteropServices;

namespace AresToys.App.Native;

internal static partial class AppNativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int WmClipboardUpdate = 0x031D;

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>EnumWindows callback. Return true to keep enumerating, false to stop. Used by
    /// <see cref="AresToys.App.Services.TargetWindowTracker"/> to walk the top-level Z-order
    /// looking for a non-AresToys paste candidate when AresToys itself is foreground.</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    /// <summary>x64-safe accessor for the WS_EX_* style flags. user32.dll only exports
    /// <c>GetWindowLongPtrW</c> on 64-bit builds (no plain <c>GetWindowLong</c>); calling the
    /// latter raises EntryPointNotFoundException at runtime. Returns nint so the cast survives
    /// without truncation, but the WS_EX flags fit comfortably in int.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Union must be the size of the largest member (MOUSEINPUT) for SendInput's cbSize check to pass.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public const uint InputKeyboard = 1;
    public const uint KeyEventfKeyUp = 0x0002;
    public const ushort VkBack = 0x08;
    public const ushort VkControl = 0x11;
    public const ushort VkV = 0x56;
    public const ushort VkReturn = 0x0D;
    public const ushort VkTab = 0x09;
    public const ushort VkEscape = 0x1B;
    public const ushort VkUp = 0x26;
    public const ushort VkDown = 0x28;
    public const ushort VkMenu = 0x12; // Alt key — used in the SetForegroundWindow "Alt trick"
    public const ushort VkLWin = 0x5B;
    public const ushort VkRWin = 0x5C;
    public const ushort VkLShift = 0xA0;
    public const ushort VkRShift = 0xA1;
    public const ushort VkLMenu = 0xA4;
    public const ushort VkRMenu = 0xA5;
    public const ushort VkLControl = 0xA2;
    public const ushort VkRControl = 0xA3;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
}
