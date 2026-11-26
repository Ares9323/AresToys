using Microsoft.Win32;

namespace AresToys.App.Services;

/// <summary>
/// The "AresToys" entry of the Explorer right-click menu on files and folders: one cascade (the arrow on the right) that
/// holds the AresToys verbs, so they do not crowd the menu. Each verb owner adds or removes its sub verb through
/// <see cref="SetVerb"/>; the cascade goes away with its last sub verb. It holds Create wormhole
/// (<see cref="Wormholes.WormholeShellRegistration"/>), Trust file (<see cref="UnblockContextMenuRegistration"/>) and
/// the file lock verbs. Explorer lists the sub verbs by key name, not by label. New file from template stays at the
/// top level of the menu.
/// <code>
/// HKCU\Software\Classes\*\shell\AresToys.Menu                       (files)
/// HKCU\Software\Classes\Directory\shell\AresToys.Menu               (folders)
/// HKCU\Software\Classes\Directory\Background\shell\AresToys.Menu   (the background of an open folder)
///     MUIVerb          = "AresToys"
///     Icon             = "&lt;exe&gt;"
///     SubCommands      = ""          (empty: the sub verbs live in the nested shell key)
///     MultiSelectModel = "Player"    (shown for any number of selected items)
///     shell\&lt;verb&gt;\
///         MUIVerb = "&lt;label&gt;", MultiSelectModel = "Player"
///         command\(default) = "&lt;command&gt;"
/// </code>
/// Per user (HKCU), no admin. On Windows 11 it appears under "Show more options", like every registry verb.
/// </summary>
public static class AresToysSubmenuRegistration
{
    /// <summary>Where a sub verb shows: on files, on folders, or both.</summary>
    [Flags]
    public enum Targets
    {
        None = 0,
        Files = 1,
        Folders = 2,
        Background = 4,
    }

    private const string FilesRoot = @"Software\Classes\*\shell\AresToys.Menu";
    private const string FoldersRoot = @"Software\Classes\Directory\shell\AresToys.Menu";
    private const string BackgroundRoot = @"Software\Classes\Directory\Background\shell\AresToys.Menu";
    private const string Label = "AresToys";
    private const string SubVerbsKeyName = "shell";
    private const string CommandKeyName = "command";
    private const string MultiSelectModel = "Player";

    /// <summary>The command of sub verb <paramref name="verb"/> (files first, then folders), or null when it is not registered.</summary>
    public static string? CommandOf(string verb)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{FilesRoot}\{SubVerbsKeyName}\{verb}\{CommandKeyName}")
                ?? Registry.CurrentUser.OpenSubKey($@"{FoldersRoot}\{SubVerbsKeyName}\{verb}\{CommandKeyName}");
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }

    /// <summary>Writes sub verb <paramref name="verb"/> (a key name, not shown) under the cascade on
    /// <paramref name="targets"/> and removes it from the others. Throws on registry failures.</summary>
    public static void SetVerb(string verb, string label, string command, Targets targets)
        => SetVerb(verb, label, target => (targets & target) != 0 ? command : null);

    /// <summary>Writes sub verb <paramref name="verb"/> with the command <paramref name="commandFor"/> gives for each
    /// target (one of <see cref="Targets"/>), and removes it where that is null. Throws on registry failures.</summary>
    public static void SetVerb(string verb, string label, Func<Targets, string?> commandFor)
    {
        var exe = ExplorerContextMenuRegistration.CurrentExePath();
        foreach (var (root, target) in Roots())
        {
            var command = commandFor(target);
            if (command is null)
            {
                RemoveVerb(root, verb);
                continue;
            }

            using var cascade = Registry.CurrentUser.CreateSubKey(root);
            cascade.SetValue("MUIVerb", Label);
            cascade.SetValue("Icon", exe);
            cascade.SetValue("SubCommands", string.Empty);
            cascade.SetValue("MultiSelectModel", MultiSelectModel);
            using var key = cascade.CreateSubKey($@"{SubVerbsKeyName}\{verb}");
            // A single '&' in a menu text marks an accelerator; double it to show the literal character.
            key.SetValue("MUIVerb", label.Replace("&", "&&", StringComparison.Ordinal));
            key.SetValue("MultiSelectModel", MultiSelectModel);
            // A build of 2026-11-23 wrote an AppliesTo filter that hid the verb everywhere: clear it.
            key.DeleteValue("AppliesTo", throwOnMissingValue: false);
            using var commandKey = key.CreateSubKey(CommandKeyName);
            commandKey.SetValue(null, command);
        }

        ExplorerContextMenuRegistration.NotifyShell();
    }

    /// <summary>Removes sub verb <paramref name="verb"/> everywhere, and the cascade where it was the last one. Best effort.</summary>
    public static void RemoveVerb(string verb)
    {
        foreach (var (root, _) in Roots())
        {
            RemoveVerb(root, verb);
        }

        ExplorerContextMenuRegistration.NotifyShell();
    }

    private static void RemoveVerb(string root, string verb)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{SubVerbsKeyName}\{verb}", throwOnMissingSubKey: false);
            bool empty;
            using (var subVerbs = Registry.CurrentUser.OpenSubKey($@"{root}\{SubVerbsKeyName}"))
            {
                empty = subVerbs is null || subVerbs.SubKeyCount == 0;
            }

            if (empty)
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            }
        }
        catch { /* best effort, like the other verbs */ }
    }

    private static IEnumerable<(string Root, Targets Target)> Roots()
    {
        yield return (FilesRoot, Targets.Files);
        yield return (FoldersRoot, Targets.Folders);
        yield return (BackgroundRoot, Targets.Background);
    }
}
