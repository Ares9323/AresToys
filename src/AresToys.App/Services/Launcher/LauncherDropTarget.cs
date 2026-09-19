using System.IO;

namespace AresToys.App.Services.Launcher;

/// <summary>The cell fields a dropped (or picked) item should produce. Kept separate from the
/// drop handler so the mapping rules are testable without a window: everything here is decided
/// from the path alone plus what the shell can tell us about it.</summary>
/// <param name="Path">What the cell launches.</param>
/// <param name="Arguments">Command-line arguments, lifted out of a shortcut when it carries any.</param>
/// <param name="Label">Cell caption.</param>
/// <param name="IconPath">Icon override — empty unless the source declared one of its own.</param>
/// <param name="IconIndex">Which icon inside <paramref name="IconPath"/>, for multi-icon containers.</param>
/// <param name="WindowMode">Initial window state, lifted from a shortcut's "Run:" setting.</param>
/// <param name="RunAsAdmin">Elevation, lifted from a shortcut's Advanced → "Run as administrator".</param>
public sealed record LauncherDropTarget(
    string Path,
    string Arguments,
    string Label,
    string IconPath = "",
    int IconIndex = 0,
    LauncherWindowMode WindowMode = LauncherWindowMode.Normal,
    bool RunAsAdmin = false)
{
    /// <summary>Translate a shortcut's SW_* "Run:" setting into the launcher's window mode.
    /// Anything unexpected maps to Normal — a shortcut can't ask for Hidden, that one only
    /// exists as a launcher/workflow choice.</summary>
    public static LauncherWindowMode WindowModeFromShowCommand(int showCommand) => showCommand switch
    {
        ShellShortcut.ShowMaximized => LauncherWindowMode.Maximized,
        ShellShortcut.ShowMinimized or 2 or 6 => LauncherWindowMode.Minimized,  // SW_SHOWMINIMIZED / SW_MINIMIZE
        _ => LauncherWindowMode.Normal,
    };

    /// <summary>Work out what a cell should hold for a dropped item.
    ///
    /// Three cases get special treatment:
    /// <list type="bullet">
    /// <item>A packaged app dragged off the Start menu arrives as a bare AppUserModelID and only
    /// runs through its <c>shell:AppsFolder\</c> form — see <see cref="PackagedAppPath"/>.</item>
    /// <item>A <c>.lnk</c> is unwrapped to the target it points at, with its arguments moved into
    /// the Arguments field (issue #12). Storing the shortcut instead means the cell breaks the
    /// day the .lnk is tidied away, and its arguments stay invisible and uneditable.</item>
    /// <item>Everything else is stored as-is.</item>
    /// </list>
    /// A shortcut we can't usefully unwrap — corrupt, or pointing at a target that isn't there
    /// (MSI-advertised shortcuts resolve to nothing until the app is repaired) — stays a
    /// shortcut, because ShellExecute still does the right thing with it.</summary>
    public static LauncherDropTarget Resolve(string? droppedPath)
    {
        if (string.IsNullOrWhiteSpace(droppedPath))
            return new LauncherDropTarget(string.Empty, string.Empty, string.Empty);

        var dropped = droppedPath.Trim();

        var packaged = PackagedAppPath.Normalize(dropped);
        if (PackagedAppPath.IsAppsFolderPath(packaged))
        {
            var appName = PackagedAppPath.TryGetDisplayName(packaged)
                          ?? PackagedAppPath.FallbackLabel(packaged)
                          ?? string.Empty;
            return new LauncherDropTarget(packaged, string.Empty, appName);
        }

        // The label tracks what the user dragged, not what it unwraps to: a Start-menu
        // "Google Chrome.lnk" should caption the cell "Google Chrome", not "chrome".
        var label = DeriveLabel(dropped);

        var link = ShellShortcut.TryRead(dropped);
        if (link is null) return new LauncherDropTarget(dropped, string.Empty, label);

        var target = link.TargetPath;
        if (string.IsNullOrWhiteSpace(target) || !TargetExists(target))
            return new LauncherDropTarget(dropped, string.Empty, label);

        // Only carry the icon over when the shortcut names one of its own. Shortcuts normally
        // point their icon location at the target, and pinning that as an override would freeze
        // the cell to an icon the shell resolves for free anyway.
        var icon = link.IconPath;
        var hasOwnIcon = !string.IsNullOrWhiteSpace(icon)
                         && !string.Equals(icon, target, StringComparison.OrdinalIgnoreCase);

        return new LauncherDropTarget(
            target,
            link.Arguments,
            label,
            hasOwnIcon ? icon : string.Empty,
            hasOwnIcon ? link.IconIndex : 0,
            WindowModeFromShowCommand(link.ShowCommand),
            link.RunAsAdministrator);
    }

    /// <summary>Pick a caption from a path: the directory name for folders, the filename without
    /// its extension for everything else (so "Google Chrome.lnk" → "Google Chrome"). The user can
    /// always rename the cell from the edit dialog.</summary>
    private static string DeriveLabel(string path)
    {
        try
        {
            if (Directory.Exists(path)) return new DirectoryInfo(path).Name;
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Does the shortcut's target still exist? Environment variables get expanded first
    /// — shortcuts under the Start menu routinely store <c>%ProgramFiles%</c>-style paths.</summary>
    private static bool TargetExists(string target)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(target);
            return File.Exists(expanded) || Directory.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }
}
