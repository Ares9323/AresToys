using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

        // Explorer names a fresh shortcut "Hardware and Sound - Shortcut"; the suffix says what
        // the file is, not what it opens, so it has no place on the cell caption.
        label = StripShortcutSuffix(label);

        // Advertised (MSI) shortcuts report the product's icon file as their path. Unwrapping
        // that would store a cell that opens an .ico, so the .lnk stays the target.
        if (link.IsAdvertised) return new LauncherDropTarget(dropped, string.Empty, label);

        var target = string.IsNullOrWhiteSpace(link.TargetPath)
            ? ShellItemTarget(link.TargetParsingName)
            : link.TargetPath;
        if (string.IsNullOrWhiteSpace(target)
            || (!IsShellNamespaceTarget(target) && !TargetExists(target)))
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

    /// <summary>CLSID of the Applications virtual folder, the one <c>shell:AppsFolder</c> names.
    /// A shortcut to a packaged app reports its target as <c>::{this}\&lt;AUMID&gt;</c>.</summary>
    private const string AppsFolderParsingPrefix = @"::{4234D49B-0245-4DF3-B780-3893943456E1}\";

    /// <summary>Turn the parsing name of a path-less shortcut target into something
    /// ShellExecute and the icon service both accept. Virtual items (<c>::{GUID}\…</c>, e.g.
    /// a Control Panel page) get the <c>shell:</c> moniker in front, packaged apps their usual
    /// <c>shell:AppsFolder\</c> form. Anything else is a plain path the shell only knew by ID
    /// list, returned as is so the caller's existence check still applies. Empty in, empty
    /// out.</summary>
    public static string ShellItemTarget(string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return string.Empty;
        var name = parsingName.Trim();
        if (name.StartsWith(AppsFolderParsingPrefix, StringComparison.OrdinalIgnoreCase)
            && name.Length > AppsFolderParsingPrefix.Length)
            return PackagedAppPath.AppsFolderPrefix + name[AppsFolderParsingPrefix.Length..];
        if (name.StartsWith("::{", StringComparison.Ordinal)) return "shell:" + name;
        return name;
    }

    /// <summary>A <c>shell:</c> moniker: lives in the shell namespace, not on disk, so there is
    /// no file to check for.</summary>
    private static bool IsShellNamespaceTarget(string target) =>
        target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Suffixes Explorer appends when it names a new shortcut, for the languages we
    /// can't read from the running system. The live one comes from shell32 (see
    /// <see cref="SystemShortcutSuffix"/>); these cover shortcuts made on a machine with a
    /// different UI language and copied over.</summary>
    private static readonly string[] KnownShortcutSuffixes =
    [
        " - Shortcut",
        " - Collegamento",
        " - Verknüpfung",
        " - Raccourci",
        " - Acceso directo",
        " - Atalho",
        " - Snelkoppeling",
        " - Skrót",
        " - Zástupce",
        " - Genväg",
        " - Ярлык",
    ];

    private static readonly Lazy<string?> SystemShortcutSuffix = new(ReadSystemShortcutSuffix);

    /// <summary>Remove the " - Shortcut" tail Explorer gives a freshly created shortcut, along
    /// with the " (2)" counter it adds to duplicates. Only a trailing suffix is touched, and
    /// never when it's all there is: "Shortcut tools" and a file literally named " - Shortcut"
    /// keep their names.</summary>
    public static string StripShortcutSuffix(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;

        var system = SystemShortcutSuffix.Value;
        IEnumerable<string> suffixes = string.IsNullOrEmpty(system)
            ? KnownShortcutSuffixes
            : KnownShortcutSuffixes.Prepend(system);

        foreach (var suffix in suffixes)
        {
            var match = Regex.Match(
                label,
                "^(?<name>.*\\S)" + Regex.Escape(suffix) + @"(?: \(\d+\))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups["name"].Value;
        }
        return label;
    }

    /// <summary>Read the naming template Explorer itself uses ("%s - Shortcut ().lnk" on an
    /// English system, "%s - Collegamento ().lnk" on an Italian one) from shell32's string
    /// table and keep the part between the name and the counter. Null when the resource isn't
    /// there or doesn't have the expected shape.</summary>
    private static string? ReadSystemShortcutSuffix()
    {
        const uint shortcutNameTemplateId = 4154;
        const uint loadAsDataFileAndImageResource = 0x00000022;
        var module = LoadLibraryEx("shell32.dll", IntPtr.Zero, loadAsDataFileAndImageResource);
        if (module == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[256];
            var length = LoadString(module, shortcutNameTemplateId, buffer, buffer.Length);
            if (length <= 0) return null;
            var template = new string(buffer, 0, length);

            var nameAt = template.IndexOf("%s", StringComparison.Ordinal);
            var extensionAt = template.LastIndexOf(".lnk", StringComparison.OrdinalIgnoreCase);
            if (nameAt < 0 || extensionAt < nameAt + 2) return null;

            var suffix = template[(nameAt + 2)..extensionAt];
            // The "()" is where Explorer writes the duplicate counter; StripShortcutSuffix
            // handles that on its own.
            var counterAt = suffix.LastIndexOf("()", StringComparison.Ordinal);
            if (counterAt >= 0) suffix = suffix[..counterAt];
            suffix = suffix.TrimEnd();
            return suffix.Trim().Length == 0 ? null : suffix;
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadString(IntPtr hInstance, uint uID, [Out] char[] lpBuffer, int nBufferMax);

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
