using System.IO;

namespace AresToys.App.Services.NewPlus;

/// <summary>Puts a freshly created item into rename mode in the Explorer window that shows its
/// folder, so the user can type the final name right away (the New+ behaviour in PowerToys).
/// Late bound through the <c>Shell.Application</c> automation object; every failure is swallowed,
/// the item is simply left selected or not at all.</summary>
internal static class NewPlusExplorer
{
    // SVSI_SELECT | SVSI_EDIT share bit 0; EDIT is 3. Plus DESELECTOTHERS (4), ENSUREVISIBLE (8), FOCUSED (0x10).
    private const int SelectForRename = 0x3 | 0x4 | 0x8 | 0x10;
    private const int SWC_DESKTOP = 0x8;
    private const int SWFO_NEEDDISPATCH = 1;

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1000),
    ];

    /// <summary>Starts the attempt on its own STA thread and returns it at once. Explorer needs a
    /// moment to show the new item, so the thread retries a few times. The thread is a background
    /// one: a caller that is about to exit the process joins it first.</summary>
    public static Thread BeginRename(string createdPath)
    {
        var thread = new Thread(() => RenameWithRetries(createdPath)) { IsBackground = true, Name = "NewPlus-Rename" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void RenameWithRetries(string createdPath)
    {
        foreach (var delay in Delays)
        {
            Thread.Sleep(delay);
            try { if (TrySelectForRename(createdPath)) return; }
            catch { /* Explorer busy or window gone: try again */ }
        }
    }

    private static bool TrySelectForRename(string createdPath)
    {
        var folder = Path.GetDirectoryName(createdPath);
        var name = Path.GetFileName(createdPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) return false;

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;

        dynamic? view = IsDesktop(folder) ? FindDesktopView(shell) : FindFolderView(shell, folder);
        if (view is null) return false;

        dynamic item = view.Folder.ParseName(name);
        if (item is null) return false;
        view.SelectItem(item, SelectForRename);
        return true;
    }

    private static dynamic? FindFolderView(dynamic shell, string folder)
    {
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                string url = window.LocationURL;
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                if (SamePath(uri.LocalPath, folder)) return window.Document;
            }
            catch { /* a window that is not a folder view (Internet Explorer, a closing window) */ }
        }
        return null;
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
