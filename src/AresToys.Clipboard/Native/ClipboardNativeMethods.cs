using System.Runtime.InteropServices;

namespace AresToys.Clipboard.Native;

internal static partial class ClipboardNativeMethods
{
    public const uint CfText = 1;
    public const uint CfBitmap = 2;
    public const uint CfDib = 8;
    public const uint CfHdrop = 15;
    public const uint CfUnicodeText = 13;

    public const int WmClipboardUpdate = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);
}
