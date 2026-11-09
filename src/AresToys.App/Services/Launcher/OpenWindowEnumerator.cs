using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AresToys.App.Services.Launcher;

/// <summary>One row in the "pick a window" dialog. Captures everything the launcher needs to
/// later activate-if-running: window title (substring match), process name (without .exe),
/// and the optional icon (resolved from the process's MainModule path so the picker shows
/// the same shell icon the user expects from Alt+Tab).</summary>
public sealed record OpenWindowInfo(
    IntPtr Hwnd,
    string Title,
    string ProcessName,
    string? ExecutablePath,
    BitmapSource? Icon);

/// <summary>Enumerates all top-level visible windows that have a non-empty title — same set
/// the user sees in the Alt+Tab switcher. Returns a flat list of <see cref="OpenWindowInfo"/>
/// the picker dialog can bind directly. Filters out tool windows / cloaked windows so we
/// don't surface invisible junk like the Windows Shell parking lot.</summary>
public static partial class OpenWindowEnumerator
{
    public static IReadOnlyList<OpenWindowInfo> Enumerate(IconService icons)
    {
        var collected = new List<(IntPtr Hwnd, string Title, uint Pid)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;     // hidden via DWM (e.g. UWP suspended apps)
            var len = GetWindowTextLength(hwnd);
            if (len == 0) return true;            // skip windows without a title
            var buf = new char[len + 1];
            var copied = GetWindowText(hwnd, buf, buf.Length);
            if (copied <= 0) return true;
            var title = new string(buf, 0, copied);
            // CA1806 requires the return value be assigned. Plain discard `_ = call()` and
            // `_ = local` both trip a CS0266 nint conversion error inside this lambda — looks
            // like the discard token is binding to the EnumWindowsProc's IntPtr lParam by
            // overload resolution. Casting to uint dodges that and keeps the analyzer happy.
            uint threadId = GetWindowThreadProcessId(hwnd, out var pid);
            if (threadId == 0) { /* window died between enum and read; just record what we have */ }
            collected.Add((hwnd, title, pid));
            return true;
        }, IntPtr.Zero);

        // Resolve PID → process name + executable path. Process.GetProcessById can throw on
        // ephemeral processes (e.g. just exited); ignore those entries entirely. Done outside
        // the EnumWindows callback to keep the callback short — long callbacks block the
        // window manager and slow the enumeration.
        var result = new List<OpenWindowInfo>(collected.Count);
        foreach (var (hwnd, title, pid) in collected)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string? exePath = null;
                try { exePath = proc.MainModule?.FileName; } catch { /* permission denied for elevated procs */ }
                var icon = exePath is not null ? icons.GetIcon(exePath) : null;
                result.Add(new OpenWindowInfo(hwnd, title, proc.ProcessName, exePath, icon));
            }
            catch (ArgumentException) { /* process gone */ }
            catch (InvalidOperationException) { /* process gone */ }
        }
        // Sort alphabetically by title (case-insensitive) so the list is predictable across
        // calls; ties broken by process name. Keeps the picker stable as the user types.
        result.Sort((a, b) =>
        {
            var byTitle = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            return byTitle != 0 ? byTitle : string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        // DWM cloaked = the window exists but is being hidden by the compositor (UWP apps in
        // background, tablet-mode hidden windows, etc). Alt+Tab skips these too.
        const int DWMWA_CLOAKED = 14;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0)
            return cloaked != 0;
        return false;
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────────

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
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
