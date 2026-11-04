using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using AresToys.App.Services.Shell;

namespace AresToys.App.Services.NewPlus;

/// <summary>Puts a freshly created item into rename mode in the window the menu was opened from, so
/// the user can type the final name right away (the New+ behaviour in PowerToys). The window is the
/// foreground one when the process starts: an Explorer window or the desktop is driven through the
/// <c>Shell.Application</c> automation object, a file dialog (Save as, Open) through UI Automation
/// because it is not listed there. Every failure is swallowed, the item is simply left selected or
/// not at all; another window on the same folder is never touched.</summary>
internal static class NewPlusExplorer
{
    // SVSI_SELECT | SVSI_EDIT share bit 0; EDIT is 3. Plus DESELECTOTHERS (4), ENSUREVISIBLE (8), FOCUSED (0x10).
    private const int SelectForRename = 0x3 | 0x4 | 0x8 | 0x10;
    private const int SWC_DESKTOP = 0x8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint GA_ROOT = 2;
    private const byte VK_F2 = 0x71;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private const string ExplorerClass = "CabinetWClass";
    private const string DefViewClass = "SHELLDLL_DefView";

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1000),
    ];

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, [Out] char[] text, int max);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

    /// <summary>The top-level window that has the focus right now.</summary>
    internal static IntPtr ForegroundRoot() => GetAncestor(GetForegroundWindow(), GA_ROOT);

    // F2 typed while Ctrl or Shift is still held (the New folder shortcut) would be another shortcut.
    private static void WaitForModifiersReleased()
    {
        for (var i = 0; i < 30 && (Down(0x11) || Down(0x10) || Down(0x12)); i++) Thread.Sleep(50);
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>Starts the attempt on its own STA thread and returns it at once. The window to use is
    /// <paramref name="foreground"/>, the one that had the focus when the request began (the foreground
    /// one right now when omitted). The item needs a moment to show up, so the thread retries a few
    /// times. The thread is a background one: a caller that is about to exit the process joins it first.</summary>
    public static Thread BeginRename(string createdPath, IntPtr foreground = default)
    {
        if (foreground == IntPtr.Zero) foreground = ForegroundRoot();
        var thread = new Thread(() => RenameWithRetries(createdPath, foreground)) { IsBackground = true, Name = "NewPlus-Rename" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    // A context menu (class #32768) or a window that is gone is no target: the window that has the
    // focus at that moment is, because a menu pick starts this process while the menu is closing.
    private static bool IsUsableTarget(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindow(hwnd) && ClassOf(hwnd) != "#32768";

    private static void RenameWithRetries(string createdPath, IntPtr foreground)
    {
        foreach (var delay in Delays)
        {
            Thread.Sleep(delay);
            try
            {
                var target = IsUsableTarget(foreground) ? foreground : ForegroundRoot();
                var dialogView = FindFileDialogView(target);
                var done = dialogView != IntPtr.Zero
                    ? TryRenameInDialog(createdPath, target, dialogView)
                    : TrySelectForRename(createdPath, target);
                if (done) return;
            }
            catch { /* window busy or gone: try again */ }
        }
    }

    // A file dialog is any top-level window that is neither Explorer nor the desktop but hosts a
    // folder view. Returns that view's handle, or zero.
    internal static IntPtr FindFileDialogView(IntPtr root)
    {
        if (root == IntPtr.Zero) return IntPtr.Zero;
        var rootClass = ClassOf(root);
        if (rootClass is ExplorerClass or "Progman" or "WorkerW") return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumChildWindows(root, (child, _) =>
        {
            if (ClassOf(child) != DefViewClass) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    internal static string ClassOf(IntPtr hwnd)
    {
        var text = new char[256];
        var length = GetClassNameW(hwnd, text, text.Length);
        return length > 0 ? new string(text, 0, length) : string.Empty;
    }

    // Selects the item in the dialog's own list and presses F2 on it. The key goes out only while
    // the dialog is still the foreground window, so it can never rename something in another app.
    private static bool TryRenameInDialog(string createdPath, IntPtr dialog, IntPtr view)
    {
        var name = Path.GetFileName(createdPath);
        if (string.IsNullOrEmpty(name)) return false;

        var list = AutomationElement.FromHandle(view);
        foreach (var candidate in new[] { name, Path.GetFileNameWithoutExtension(name) }.Distinct())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var matches = list.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, candidate));
            foreach (AutomationElement item in matches)
            {
                if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
                ((SelectionItemPattern)pattern).Select();
                item.SetFocus();
                WaitForModifiersReleased();
                if (GetAncestor(GetForegroundWindow(), GA_ROOT) != dialog) SetForegroundWindow(dialog);
                if (GetAncestor(GetForegroundWindow(), GA_ROOT) != dialog) return true;
                keybd_event(VK_F2, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                return true;
            }
        }
        return false;
    }

    private static bool TrySelectForRename(string createdPath, IntPtr foreground)
    {
        var folder = Path.GetDirectoryName(createdPath);
        var name = Path.GetFileName(createdPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) return false;

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;

        dynamic? view = IsDesktop(folder) ? FindDesktopView(shell) : FindFolderView(shell, folder, foreground);
        if (view is null) return false;

        dynamic item = view.Folder.ParseName(name);
        if (item is null) return false;
        view.SelectItem(item, SelectForRename);
        return true;
    }

    // The window the menu came from wins; the folder alone is the fallback when no window matches the
    // foreground one (every tab of a window shares its handle, the folder tells the tabs apart).
    private static dynamic? FindFolderView(dynamic shell, string folder, IntPtr foreground)
    {
        // In an Explorer window the active tab is the one the menu came from, whatever other tabs show.
        if (ClassOf(foreground) == ExplorerClass && ExplorerWindows.FindActive(foreground) is { } active
            && SamePath(active.Path, folder))
        {
            dynamic activeWindow = active.Window;
            return activeWindow.Document;
        }
        dynamic? fallback = null;
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                string url = window.LocationURL;
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                if (!SamePath(uri.LocalPath, folder)) continue;
                if (foreground != IntPtr.Zero && new IntPtr((long)window.HWND) == foreground) return window.Document;
                fallback ??= window.Document;
            }
            catch { /* a window that is not a folder view (Internet Explorer, a closing window) */ }
        }
        return fallback;
    }

    private static dynamic? FindDesktopView(dynamic shell)
    {
        object? location = null;
        object? root = null;
        dynamic windows = shell.Windows();
        dynamic? window = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH);
        return window?.Document;
    }

    private static bool IsDesktop(string folder)
        => SamePath(folder, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
           || SamePath(folder, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
