using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AresToys.Clipboard;

public sealed partial class ForegroundProcessProbe : IForegroundProcessProbe
{
    public string? GetForegroundProcessName()
    {
        var hwnd = NativeForeground.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : ProcessNameOf(hwnd);
    }

    public string? GetForegroundWindowTitle()
    {
        var hwnd = NativeForeground.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : TitleOf(hwnd);
    }

    public ForegroundSnapshot GetForeground()
    {
        var hwnd = NativeForeground.GetForegroundWindow();
        return hwnd == IntPtr.Zero
            ? new ForegroundSnapshot(null, null)
            : new ForegroundSnapshot(ProcessNameOf(hwnd), TitleOf(hwnd));
    }

    private static string? ProcessNameOf(IntPtr hwnd)
    {
        _ = NativeForeground.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? TitleOf(IntPtr hwnd)
    {
        var len = NativeForeground.GetWindowTextLength(hwnd);
        if (len <= 0) return null;
        var buffer = new char[len + 1];
        var read = NativeForeground.GetWindowText(hwnd, buffer, buffer.Length);
        return read > 0 ? new string(buffer, 0, read) : null;
    }

    private static partial class NativeForeground
    {
        [LibraryImport("user32.dll")]
        public static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
        public static partial int GetWindowTextLength(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
    }
}
