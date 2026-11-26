using System.Globalization;
using Microsoft.Win32;
using AresToys.App.Resources;

namespace AresToys.App.Services.Wormholes;

/// <summary>The "Create wormhole" verb, first entry of the AresToys submenu of the Explorer right-click menu
/// (<see cref="AresToysSubmenuRegistration"/>), on folders and on the background of an open folder:
/// <list type="bullet">
///   <item>On a folder ITEM (in a folder view or on the desktop) it runs <c>"arestoys.exe" --create-wormhole "%1"</c>
///         with the clicked folder.</item>
///   <item>On the BACKGROUND it runs <c>"arestoys.exe" --new-wormhole-dialog</c>: no path, the user picks the source
///         folder in the New Wormhole dialog (a right click on an empty desktop area almost never means "mirror the
///         Desktop").</item>
/// </list>
/// The running primary instance (or a fresh one) catches the flag. Explorer lists the submenu by key name, so the key
/// <c>CreateWormhole</c> sorts before Unblock and UnlockFile and the verb comes first. Per user (HKCU), no admin; on
/// Windows 11 it sits under "Show more options", like every registry verb.</summary>
public static class WormholeShellRegistration
{
    private const string VerbName = "CreateWormhole";
    private const string ItemCommandLineArg = "--create-wormhole";
    private const string BackgroundCommandLineArg = "--new-wormhole-dialog";

    /// <summary>Where earlier builds put the verb, at the top level of the menu; removed on every change.</summary>
    private static readonly string[] OldTopLevelKeys =
    [
        @"Software\Classes\Directory\shell\AresToysCreateWormhole",
        @"Software\Classes\Directory\Background\shell\AresToysCreateWormhole",
    ];

    public static bool IsRegistered()
    {
        try
        {
            return AresToysSubmenuRegistration.CommandOf(VerbName) is { } command
                && command.Contains(ExplorerContextMenuRegistration.CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Writes the verb on folders and folder backgrounds. Throws on registry failures.</summary>
    public static void Register()
    {
        RemoveOldTopLevel();
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        var label = Strings.ResourceManager.GetString(nameof(Strings.WormholeMenu_Label), culture) ?? "Create wormhole";
        AresToysSubmenuRegistration.SetVerb(VerbName, label, target => target switch
        {
            // %1 = the clicked folder; the quotes survive paths with spaces.
            AresToysSubmenuRegistration.Targets.Folders => $"\"{exe}\" {ItemCommandLineArg} \"%1\"",
            AresToysSubmenuRegistration.Targets.Background => $"\"{exe}\" {BackgroundCommandLineArg}",
            _ => null,
        });
    }

    /// <summary>Removes the verb. Best effort: a missing key is fine.</summary>
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
