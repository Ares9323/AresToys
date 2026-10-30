using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AresToys.App.Resources;
using AresToys.Hotkeys;

namespace AresToys.App.Services.NewPlus;

/// <summary>
/// Ctrl+Shift+N, kept alive while the built-in "New" context menu is hidden. Windows runs that
/// shortcut through the very handler the hide switch turns off, so with the switch on the key does
/// nothing. While New+ is on and the built-in entry is hidden, this binds the key in the keyboard hook
/// for Explorer, the desktop and file dialogs (Save as, Open) only, makes the folder and puts it in
/// rename mode like the menu does. Any other window sees the key untouched (Chrome keeps its own
/// Ctrl+Shift+N). Needs AresToys running, which the menu does not.
/// </summary>
public sealed class NewPlusFolderShortcut
{
    private const string HookId = "newplus.new-folder";
    private const uint VkN = 0x4E;
    private static readonly Regex PathInName = new(@"([A-Za-z]:\\.*|\\\\.+)$", RegexOptions.Compiled);

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

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

    private readonly KeyboardHook _hook;
    private volatile bool _active;

    public NewPlusFolderShortcut(KeyboardHook hook)
    {
        _hook = hook;
        _hook.Register(HookId, HotkeyModifiers.Control | HotkeyModifiers.Shift, VkN, OnPressed, suppress: true, when: CanHandle);
    }

    /// <summary>Turns the shortcut on or off. Cheap, called on every settings sync.</summary>
    public void SetActive(bool active) => _active = active;

    // Hook thread, so only cheap window checks: the key is ours only in a folder window or file dialog.
    private bool CanHandle()
    {
        if (!_active) return false;
        var root = NewPlusExplorer.ForegroundRoot();
        return IsFolderWindow(root) || NewPlusExplorer.FindFileDialogView(root) != IntPtr.Zero;
    }

    private static bool IsFolderWindow(IntPtr root)
        => NewPlusExplorer.ClassOf(root) is "CabinetWClass" or "Progman" or "WorkerW";

    private void OnPressed()
    {
        var root = NewPlusExplorer.ForegroundRoot();
        // A thread of its own (STA): UI Automation and the shell automation object block for a while.
        var thread = new Thread(() => CreateFolder(root)) { IsBackground = true, Name = "NewPlus-NewFolder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void CreateFolder(IntPtr root)
    {
        try
        {
            var parent = CurrentFolder(root);
            if (parent is null || !Directory.Exists(parent)) return;
            var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
            var name = Strings.ResourceManager.GetString(nameof(Strings.NewPlus_DefaultFolderName), culture) ?? "New folder";
            var created = NewPlusTemplates.MakeUniquePath(Path.Combine(parent, name));
            Directory.CreateDirectory(created);
            NewPlusNative.NotifyCreated(created);
            NewPlusExplorer.BeginRename(created);
        }
        catch { /* folder not writable or window gone: nothing to put in rename mode */ }
    }

    private static string? CurrentFolder(IntPtr root)
    {
        var rootClass = NewPlusExplorer.ClassOf(root);
        if (rootClass is "Progman" or "WorkerW") return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (rootClass == "CabinetWClass") return ExplorerFolder(root);
        return DialogFolder(root);
    }

    // The window's tabs all report its handle: with several, the one whose browser window is visible is the open tab.
    private static string? ExplorerFolder(IntPtr root)
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
        if (candidates.Count == 1) return candidates[0].Path;
        foreach (var (window, path) in candidates)
            if (IsOpenTab(window)) return path;
        return null;
    }

    private static bool IsOpenTab(object window)
    {
        try
        {
            if (window is not IServiceProvider provider) return false;
            var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var oleWindow = typeof(IOleWindow).GUID;
            if (provider.QueryService(ref topLevelBrowser, ref oleWindow, out var ptr) != 0 || ptr == IntPtr.Zero) return false;
            try
            {
                var tab = (IOleWindow)Marshal.GetObjectForIUnknown(ptr);
                return tab.GetWindow(out var hwnd) == 0 && IsWindowVisible(hwnd);
            }
            finally { Marshal.Release(ptr); }
        }
        catch { return false; }
    }

    // A file dialog has no automation object; its address bar carries the path in its name.
    private static string? DialogFolder(IntPtr root)
    {
        var dialog = AutomationElement.FromHandle(root);
        var bars = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
        foreach (AutomationElement bar in bars)
        {
            var match = PathInName.Match(bar.Current.Name ?? string.Empty);
            if (match.Success && Directory.Exists(match.Value)) return match.Value;
        }
        return null;
    }
}
