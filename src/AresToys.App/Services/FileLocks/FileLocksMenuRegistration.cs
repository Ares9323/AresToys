using System.Globalization;
using AresToys.App.Resources;

namespace AresToys.App.Services.FileLocks;

/// <summary>
/// The two file lock verbs in the AresToys submenu of the Explorer right-click menu (<see cref="AresToysSubmenuRegistration"/>),
/// on files and folders, as LockHunter splits them between menu and window:
/// "Unlock file" runs <c>"arestoys.exe" --unlock-file "&lt;path&gt;"</c>: no window, it says when nothing holds the item,
/// otherwise asks once and closes the handles, opening the window only when the item stays locked;
/// "View lock state" runs <c>"arestoys.exe" --file-locks "&lt;path&gt;"</c> and opens the window (PowerToys File Locksmith
/// engine). Both are handled by App.OnStartup in the launched process, so they work with AresToys closed. The owner wants
/// them always in the submenu: registered on the first start, they stay until he turns their switch off on the Right
/// Click Menu page. The registry is the state.
/// </summary>
public static class FileLocksMenuRegistration
{
    /// <summary>The switch of the quick "Unlock file" verb, read by App.OnStartup.</summary>
    public const string CommandLineArg = "--unlock-file";

    /// <summary>The switch of the "View lock state" verb that opens the window, read by App.OnStartup.</summary>
    public const string WindowCommandLineArg = "--file-locks";

    private const string VerbName = "UnlockFile";
    private const string WindowVerbName = "UnlockFileView";
    // The key name orders the cascade (Explorer sorts by it): UnlockFileView lands right after UnlockFile. The first
    // build used IsFileLocked, which sorted before Trust file; it is removed on registration.
    private const string OldWindowVerbName = "IsFileLocked";
    private const string SettingsKey = @"Software\AresToys";
    private const string ChosenValue = "UnlockFileMenuChosen";

    /// <summary>True when the verbs point at the current exe.</summary>
    public static bool IsRegistered()
    {
        try
        {
            var exe = ExplorerContextMenuRegistration.CurrentExePath();
            return AresToysSubmenuRegistration.CommandOf(VerbName) is { } command
                && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Writes both verbs for files and folders. Throws on registry failures so the settings switch can show them.</summary>
    public static void Register()
    {
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        var targets = AresToysSubmenuRegistration.Targets.Files | AresToysSubmenuRegistration.Targets.Folders;
        var label = Strings.ResourceManager.GetString(nameof(Strings.FileLocksMenu_Label), culture) ?? "Unlock file";
        AresToysSubmenuRegistration.SetVerb(VerbName, label, $"\"{exe}\" {CommandLineArg} \"%1\"", targets);
        var windowLabel = Strings.ResourceManager.GetString(nameof(Strings.FileLocksMenu_WindowLabel), culture) ?? "View lock state";
        AresToysSubmenuRegistration.RemoveVerb(OldWindowVerbName);
        AresToysSubmenuRegistration.SetVerb(WindowVerbName, windowLabel, $"\"{exe}\" {WindowCommandLineArg} \"%1\"", targets);
    }

    /// <summary>Removes both verbs. Best effort: a missing key is fine.</summary>
    public static void Unregister()
    {
        AresToysSubmenuRegistration.RemoveVerb(VerbName);
        AresToysSubmenuRegistration.RemoveVerb(WindowVerbName);
        AresToysSubmenuRegistration.RemoveVerb(OldWindowVerbName);
    }

    /// <summary>The settings switch: registers or removes the verbs and remembers that the user chose, so startup no
    /// longer turns them on by itself.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled) Register();
        else Unregister();
        RememberChoice();
    }

    /// <summary>At startup: on by default until the user turns them off, and the labels and commands rewritten so a
    /// rename, a new verb or a language change reaches the menu. Best effort.</summary>
    public static void EnsureAtStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey);
            var chosen = key?.GetValue(ChosenValue) is int value && value != 0;
            if (!chosen || IsRegistered()) Register();
            if (!chosen) RememberChoice();
        }
        catch { /* best effort */ }
    }

    private static void RememberChoice()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(ChosenValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
    }
}
