using System.IO;
using System.Runtime.InteropServices;
using AresToys.App.Services.NewPlus;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Shell;

/// <summary>The one place that finds the ACTIVE tab of an Explorer window and reads from it: the folder it
/// shows, its selection, and its items in the order the user sees them (sort and grouping applied).
/// Used by New+ (Ctrl+Shift+N, Ctrl+D, rename after creation), <see cref="ExplorerSelection"/> and Peek.
/// The shell window objects are dynamic COM, so callers run on an STA thread. Every COM call is guarded:
/// a failure gives an empty result or null, never an exception.</summary>
public static class ExplorerWindows
{
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;
    private const uint SVGIO_ALLVIEW = 2;
    /// <summary>Without it Items returns the folder's internal order, not the order Explorer shows (sort and groups).</summary>
    private const uint SVGIO_FLAG_VIEWORDER = 0x80000000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int SWC_DESKTOP = 0x8;
    private const int SWFO_NEEDDISPATCH = 1;

    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
    }

    // Vtable: IUnknown, IOleWindow (2), then IShellBrowser's own methods up to QueryActiveShellView.
    // The slots before it are never called; they only keep the order exact.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int InsertMenusSB();
        [PreserveSig] int SetMenuSB();
        [PreserveSig] int RemoveMenusSB();
        [PreserveSig] int SetStatusTextSB();
        [PreserveSig] int EnableModelessSB();
        [PreserveSig] int TranslateAcceleratorSB();
        [PreserveSig] int BrowseObject();
        [PreserveSig] int GetViewStateStream();
        [PreserveSig] int GetControlWindow();
        [PreserveSig] int SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    // IFolderView (the base of IFolderView2): Items(SVGIO_ALLVIEW) lists the view in the order shown.
    // Slots 1-5 are never called; they only keep the order exact.
    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode();
        [PreserveSig] int SetCurrentViewMode();
        [PreserveSig] int GetFolder();
        [PreserveSig] int Item();
        [PreserveSig] int ItemCount();
        [PreserveSig] int Items(uint flags, ref Guid riid, out IntPtr items);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler();
        [PreserveSig] int GetPropertyStore();
        [PreserveSig] int GetPropertyDescriptionList();
        [PreserveSig] int GetAttributes();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler();
        [PreserveSig] int GetParent();
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    }

    private const uint SFGAO_FOLDER = 0x20000000;
    private const uint SFGAO_STREAM = 0x00400000;

    // The shell item already tells a file from a folder (a zip is folder and stream, and counts as a file), so no
    // File.Exists per item; the file system is asked only when the shell cannot tell.
    private static bool IsFileItem(IShellItem item, string path)
    {
        if (item.GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, out var attributes) < 0) return File.Exists(path);
        return (attributes & SFGAO_FOLDER) == 0 || (attributes & SFGAO_STREAM) != 0;
    }

    /// <summary>The shell window object (a folder view) of the active tab of the Explorer window <paramref name="root"/>,
    /// with the folder it shows. Null when it shows no file system folder or cannot be told apart.</summary>
    internal static (object Window, string Path)? FindActive(IntPtr root)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;

        var candidates = new List<(object Window, string Path)>();
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                if (new IntPtr((long)window.HWND) != root) continue;
                string url = window.LocationURL;
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                candidates.Add(((object)window, uri.LocalPath));
            }
            catch { /* not a folder view */ }
        }
        // The tabs of one window all report its handle: with several, the active one is told apart below.
        if (candidates.Count == 1) return candidates[0];
        foreach (var candidate in candidates)
            if (IsOpenTab(candidate.Window, root)) return candidate;
        return null;
    }

    /// <summary>The folder view <paramref name="root"/> stands for: the desktop when it is the desktop window (class
    /// Progman or WorkerW), else the active tab of the Explorer window (<see cref="FindActive"/>). The desktop is not
    /// in <c>Shell.Application.Windows()</c>, it is asked for apart (SWC_DESKTOP); its folder is the user's Desktop.</summary>
    private static (object Window, string Path)? FindView(IntPtr root)
        => NewPlusExplorer.ClassOf(root) is "Progman" or "WorkerW" ? FindDesktop() : FindActive(root);

    /// <summary>The desktop's shell window (dispatch object) and the user's Desktop folder; null when the shell has none.
    /// The one place that asks for it (New+ selects a new file on the desktop through it too).</summary>
    internal static (object Window, string Path)? FindDesktop()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;

        object? location = null;
        object? where = null;
        dynamic windows = shell.Windows();
        dynamic? window = windows.FindWindowSW(ref location, ref where, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH);
        if (window is null) return null;
        return ((object)window, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    /// <summary>The paths selected in the active tab of the Explorer window <paramref name="root"/> (the foreground
    /// Explorer window when omitted) or on the desktop when <paramref name="root"/> is the desktop window, folders
    /// included, in selection order. Empty when there is no such window, nothing is selected or Explorer's
    /// automation surface fails. Run on an STA thread.</summary>
    public static IReadOnlyList<string> GetActiveSelection(IntPtr root = default, ILogger? logger = null)
    {
        try
        {
            if (root == IntPtr.Zero) root = NewPlusExplorer.ForegroundRoot();
            if (root == IntPtr.Zero || FindView(root) is not { } found) return Array.Empty<string>();

            var paths = new List<string>();
            dynamic window = found.Window;
            foreach (dynamic item in window.Document.SelectedItems())
            {
                string? path = item.Path as string;
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException)
        {
            logger?.LogWarning(ex, "ExplorerWindows: reading the selection of the active tab failed");
            return Array.Empty<string>();
        }
    }

    /// <summary>All file system items of the active tab of the Explorer window <paramref name="root"/> (or of the
    /// desktop when it is the desktop window) in the order
    /// the user sees them (the view's sort and grouping), folders left out, with the index of
    /// <paramref name="selectedPath"/> in that list (case-insensitive; -1 when it is not among them). Null when
    /// the view cannot be read. Run on an STA thread.</summary>
    public static (IReadOnlyList<string> Items, int Index)? GetViewItems(IntPtr root, string selectedPath)
    {
        object? view = null;
        IShellItemArray? array = null;
        try
        {
            if (FindView(root) is not { } found) return null;
            if (QueryTopLevel(found.Window, typeof(IShellBrowser).GUID) is not IShellBrowser browser) return null;
            if (browser.QueryActiveShellView(out view) != 0 || view is not IFolderView folderView) return null;

            var arrayIid = typeof(IShellItemArray).GUID;
            if (folderView.Items(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, ref arrayIid, out var arrayPtr) != 0 || arrayPtr == IntPtr.Zero) return null;
            try { array = (IShellItemArray)Marshal.GetObjectForIUnknown(arrayPtr); }
            finally { Marshal.Release(arrayPtr); }

            if (array.GetCount(out var count) != 0) return null;
            var items = new List<string>((int)count);
            var index = -1;
            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out var item) != 0) continue;
                try
                {
                    if (item.GetDisplayName(SIGDN_FILESYSPATH, out var namePtr) != 0 || namePtr == IntPtr.Zero) continue;
                    string? path;
                    try { path = Marshal.PtrToStringUni(namePtr); }
                    finally { CoTaskMemFree(namePtr); }
                    if (string.IsNullOrEmpty(path) || !IsFileItem(item, path)) continue; // folders and virtual items
                    if (index < 0 && string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase)) index = items.Count;
                    items.Add(path);
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            return (items, index);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException)
        {
            return null; // no logger on this call: the view is gone or not a standard Explorer view
        }
        finally
        {
            if (array is not null) Marshal.ReleaseComObject(array);
            if (view is not null) Marshal.ReleaseComObject(view);
        }
    }

    // Asks the shell window object for the top level browser of its tab as the interface <paramref name="iid"/>.
    private static object? QueryTopLevel(object window, Guid iid)
    {
        if (window is not IServiceProvider provider) return null;
        var service = SidTopLevelBrowser;
        if (provider.QueryService(ref service, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(ptr); }
        finally { Marshal.Release(ptr); }
    }

    // Every tab's window stays visible, so visibility cannot tell them apart: the open tab is the topmost
    // ShellTabWindowClass child of the Explorer window (Explorer raises a tab when it is activated).
    private static bool IsOpenTab(object window, IntPtr root)
    {
        try
        {
            if (QueryTopLevel(window, typeof(IOleWindow).GUID) is not IOleWindow tab) return false;
            return tab.GetWindow(out var hwnd) == 0 && hwnd != IntPtr.Zero && hwnd == TopmostTab(root);
        }
        catch { return false; }
    }

    private static IntPtr TopmostTab(IntPtr root)
    {
        for (var child = GetWindow(root, GW_CHILD); child != IntPtr.Zero; child = GetWindow(child, GW_HWNDNEXT))
            if (NewPlusExplorer.ClassOf(child) == "ShellTabWindowClass" && IsWindowVisible(child)) return child;
        return IntPtr.Zero;
    }
}
