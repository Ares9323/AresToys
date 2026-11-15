using System.Globalization;
using Microsoft.Win32;
using AresToys.App.Resources;

namespace AresToys.App.Services;

/// <summary>
/// The "Unblock" verb in the AresToys submenu of the Explorer right-click menu (<see cref="AresToysSubmenuRegistration"/>),
/// on files and folders. It removes the Mark of the Web (<see cref="MarkOfTheWeb"/>) so Explorer previews downloaded files
/// again; on a folder it goes through every file below it. Clicking it runs <c>"arestoys.exe" --unblock "&lt;path&gt;"</c>,
/// which App.OnStartup handles in the launched process and exits: the running instance is never involved, so the verb
/// works with AresToys closed. The registry is the state: the settings switch reads <see cref="IsRegistered"/>.
/// </summary>
public static class UnblockContextMenuRegistration
{
    /// <summary>The command line switch the verb passes, read by App.OnStartup.</summary>
    public const string CommandLineArg = "--unblock";

    private const string VerbName = "Unblock";

    /// <summary>Where the first build put the verb, at the top level of the menu; removed on every change.</summary>
    private static readonly string[] OldTopLevelKeys =
    [
        @"Software\Classes\*\shell\AresToysUnblock",
        @"Software\Classes\Directory\shell\AresToysUnblock",
    ];

    /// <summary>True when the verb points at the current exe; one left by another install location reads as not registered.</summary>
    public static bool IsRegistered()
    {
        try
        {
            return AresToysSubmenuRegistration.CommandOf(VerbName) is { } command
                && command.Contains(ExplorerContextMenuRegistration.CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Writes the verb for files and folders. Throws on registry failures so the settings switch can show them.</summary>
    public static void Register()
    {
        RemoveOldTopLevel();
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        var label = Strings.ResourceManager.GetString(nameof(Strings.UnblockMenu_Label), culture) ?? "Unblock";
        // %1 = the path Explorer hands us; the quotes survive paths with spaces.
        AresToysSubmenuRegistration.SetVerb(VerbName, label, $"\"{exe}\" {CommandLineArg} \"%1\"",
            AresToysSubmenuRegistration.Targets.Files | AresToysSubmenuRegistration.Targets.Folders);
    }

    /// <summary>Removes the verb from files and folders. Best effort: a missing key is fine.</summary>
    public static void Unregister()
    {
        RemoveOldTopLevel();
        AresToysSubmenuRegistration.RemoveVerb(VerbName);
    }

    private static void RemoveOldTopLevel()
    {
        foreach (var path in OldTopLevelKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch { /* best effort */ }
        }
    }
}
