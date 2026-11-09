using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AresToys.App.Services.Launcher;

/// <summary>"Activate if running" helper for launcher cells. Given an optional window-title
/// substring and an optional process-name substring, looks for an already-running match and
/// brings it to the foreground; returns true if it activated something. Both matchers run
/// case-insensitive substring; either one being non-empty is enough to opt into the search.
/// MaxLauncher exposes the same UX as "WindowTitle / ProcessName" cell options.</summary>
public static partial class WindowActivator
{
    public static bool TryActivate(string? windowTitle, string? processName)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(windowTitle);
        var hasProc  = !string.IsNullOrWhiteSpace(processName);
        if (!hasTitle && !hasProc) return false;

        // Process-name path is fastest — Process.GetProcessesByName takes the (basename without
        // .exe) and returns every running instance directly. Try this first when set; if it
        // turns up nothing, fall back to the EnumWindows title scan.
        if (hasProc)
        {
            var trimmed = TrimExeSuffix(processName!.Trim());
            try
            {
                foreach (var proc in Process.GetProcessesByName(trimmed))
                {
                    using (proc)
                    {
                        var hwnd = proc.MainWindowHandle;
                        if (hwnd == IntPtr.Zero) continue;
                        if (Activate(hwnd)) return true;
                    }
                }
            }
            catch { /* ignore — Process API can throw on permission edge cases */ }
        }

        if (hasTitle)
        {
            var needle = windowTitle!.Trim();
            IntPtr foundHwnd = IntPtr.Zero;
            // EnumWindows visits every top-level HWND; we keep the first visible one whose
            // title contains the needle. Bool-returning callback: false stops enumeration.
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var buffer = new char[len + 1];
                var copied = GetWindowText(hwnd, buffer, buffer.Length);
                if (copied <= 0) return true;
                var title = new string(buffer, 0, copied);
                if (title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (foundHwnd != IntPtr.Zero && Activate(foundHwnd)) return true;
        }

        return false;
    }

    private static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        // Restore minimized windows first — SetForegroundWindow alone won't un-minimize. We do
        // an unconditional SW_RESTORE because it's a no-op on already-normal windows.
        ShowWindow(hwnd, SW_RESTORE);
        return SetForegroundWindow(hwnd);
    }

    /// <summary>Drop a trailing ".exe" so users can write "notepad.exe" or "notepad" and the
    /// matcher behaves the same way. Process.GetProcessesByName expects the bare name.</summary>
    private static string TrimExeSuffix(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // ── Win32 interop ──────────────────────────────────────────────────────────────

    private const int SW_RESTORE = 9;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, [Out] char[] text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
