using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AresToys.App.Resources;
using AresToys.App.Services.Shell;
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
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        // An elevated process cannot drive a normal Explorer through the shell automation object, so the
        // job goes to a copy of this exe running with the shell's token (as the New+ menu verb does).
        if (TryRunUnelevated(NewPlusHeadless.NewFolderSwitch, root, culture)) return;
        // A thread of its own (STA): UI Automation and the shell automation object block for a while.
        var thread = new Thread(() => CreateFolder(root, culture)) { IsBackground = true, Name = "NewPlus-NewFolder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static bool TryRunUnelevated(string headlessSwitch, IntPtr root, CultureInfo culture)
    {
        // Only for Explorer: dialogs go through UI Automation, which an unelevated copy could not use on an elevated app's dialog.
        if (!ElevationService.IsCurrentProcessElevated || NewPlusExplorer.ClassOf(root) != "CabinetWClass") return false;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var command = $"\"{exe}\" {headlessSwitch} {root.ToInt64()} {culture.Name}";
            return ElevationService.TryStartWithShellToken(null, command, Path.GetDirectoryName(exe), -1, out _);
        }
        catch { return false; }
    }

    /// <summary>Makes the folder in the window <paramref name="root"/> shows and starts its rename. Returns the
    /// rename thread (null when there was nothing to do) so a headless caller can wait for it.</summary>
    internal static Thread? CreateFolder(IntPtr root, CultureInfo culture)
    {
        try
        {
            var parent = CurrentFolder(root);
            if (parent is null || !Directory.Exists(parent)) return null;
            var name = Strings.ResourceManager.GetString(nameof(Strings.NewPlus_DefaultFolderName), culture) ?? "New folder";
            var created = NewPlusTemplates.MakeUniquePath(Path.Combine(parent, name));
            Directory.CreateDirectory(created);
            NewPlusNative.NotifyCreated(created);
            return NewPlusExplorer.BeginRename(created, root);
        }
        catch { return null; /* folder not writable or window gone: nothing to put in rename mode */ }
    }

    private static string? CurrentFolder(IntPtr root)
    {
        var rootClass = NewPlusExplorer.ClassOf(root);
        if (rootClass is "Progman" or "WorkerW") return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (rootClass == "CabinetWClass") return ExplorerFolder(root);
        return DialogFolder(root);
    }

    // The window's tabs all report its handle: ExplorerWindows tells the active one apart.
    private static string? ExplorerFolder(IntPtr root) => ExplorerWindows.FindActive(root)?.Path;

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
