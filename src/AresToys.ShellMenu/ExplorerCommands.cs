using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AresToys.App.Services;
using Microsoft.Win32;

namespace AresToys.ShellMenu;

/// <summary>
/// One entry of the Windows 11 context menu that mirrors a registry verb AresToys already writes for the classic menu
/// (<see cref="VerbSource"/> says which key). The registry stays the single state: the title, the icon, the sub verbs
/// and their commands are read from it each time the menu opens, so a verb an AresToys feature adds or removes shows
/// up here with no code in this DLL. A verb with a SubCommands value is a cascade (one <see cref="RegistryVerbCommand"/>
/// per sub key, in key name order, as Explorer orders them); a verb without one runs its own command.
/// </summary>
[GeneratedComClass]
internal sealed partial class RegistryVerbCommand : IExplorerCommand, IObjectWithSite
{
    private const string SubVerbsKeyName = "shell";
    private const string CommandKeyName = "command";
    // The Trust file sub verb (UnblockContextMenuRegistration): shown on files only when one carries the Mark of the Web.
    private const string UnblockVerb = "Unblock";

    private readonly VerbSource _source;
    private readonly string? _subVerb;
    private readonly string? _folderFromParent;
    private nint _site;
    private IReadOnlyList<VerbTarget> _lastTargets = [];

    /// <summary>The top level entry of <paramref name="source"/>.</summary>
    public RegistryVerbCommand(VerbSource source) => _source = source;

    private RegistryVerbCommand(VerbSource source, string subVerb, string? folderFromParent)
    {
        _source = source;
        _subVerb = subVerb;
        _folderFromParent = folderFromParent;
    }

    public int GetTitle(IShellItemArray? items, out nint name)
    {
        name = 0;
        try
        {
            var title = ReadVerbKey(FirstOf(Targets(items)),
                key => MenuText(IndirectString.Resolve(key.GetValue("MUIVerb") as string ?? key.GetValue(null) as string)));
            if (string.IsNullOrEmpty(title)) return HResult.E_FAIL;
            name = Marshal.StringToCoTaskMemUni(title);
            return HResult.S_OK;
        }
        catch
        {
            return HResult.E_FAIL;
        }
    }

    public int GetIcon(IShellItemArray? items, out nint icon)
    {
        icon = 0;
        try
        {
            // Another app's verb often has no Icon value (the classic menu then shows none): its program's icon then.
            var path = ReadVerbKey(FirstOf(Targets(items)), key => IconResource(key.GetValue("Icon") as string)
                ?? (_source.IconFromCommand ? IconResource(CommandExe(ReadCommand(key))) : null));
            if (path is null) return HResult.E_NOTIMPL;
            icon = Marshal.StringToCoTaskMemUni(path);
            return HResult.S_OK;
        }
        catch
        {
            return HResult.E_NOTIMPL;
        }
    }

    public int GetToolTip(IShellItemArray? items, out nint infoTip)
    {
        infoTip = 0;
        return HResult.E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid commandName)
    {
        commandName = Guid.Empty;
        return HResult.E_NOTIMPL;
    }

    public int GetState(IShellItemArray? items, bool okToBeSlow, out uint commandState)
    {
        try
        {
            var targets = Targets(items);
            commandState = IsVisible(targets) ? ExplorerCommandState.Enabled : ExplorerCommandState.Hidden;
        }
        catch
        {
            commandState = ExplorerCommandState.Hidden;
        }

        return HResult.S_OK;
    }

    public int Invoke(IShellItemArray? items, nint bindContext)
    {
        try
        {
            var targets = Targets(items);
            if (targets.Count == 0) return HResult.E_FAIL;
            // Explorer hands its foreground right to this surrogate; pass it on, or the AresToys window opens behind.
            AllowSetForegroundWindow(AnyProcess);
            // MultiSelectModel Player and Document both mean one process per selected item, as Explorer runs them.
            foreach (var target in targets)
            {
                var command = ReadVerbKey(target, ReadCommand);
                if (!string.IsNullOrWhiteSpace(command)) Launch(command, target.Path, _source.Background);
            }

            return HResult.S_OK;
        }
        catch
        {
            return HResult.E_FAIL;
        }
    }

    public int GetFlags(out uint flags)
    {
        flags = ExplorerCommandFlags.Default;
        try
        {
            if (_subVerb is null && ReadVerbKey(FirstOf(_lastTargets) ?? DefaultTarget(), key => key.GetValue("SubCommands") is not null))
            {
                flags = ExplorerCommandFlags.HasSubCommands;
            }
        }
        catch
        {
            // A plain verb then.
        }

        return HResult.S_OK;
    }

    public int EnumSubCommands(out IEnumExplorerCommand? enumerator)
    {
        enumerator = null;
        if (_subVerb is not null) return HResult.E_NOTIMPL;
        try
        {
            var targets = _lastTargets.Count > 0 ? _lastTargets : Targets(null);
            var names = SubVerbNames(targets);
            if (names.Count == 0) return HResult.E_NOTIMPL;
            var folder = _source.Background ? FirstOf(targets)?.Path : null;
            var commands = names.Select(name => (IExplorerCommand)new RegistryVerbCommand(_source, name, folder)).ToList();
            // The verbs that sit next to the cascade in the classic menu go inside it here (each hides itself where it
            // does not apply): Windows 11 folds every entry of one package into a submenu named after the package, so
            // separate entries would show as AresToys > AresToys, Image Resizer.
            commands.AddRange(_source.Extras.Select(extra => (IExplorerCommand)new RegistryVerbCommand(extra)));
            enumerator = new ExplorerCommandEnumerator(commands);
            return HResult.S_OK;
        }
        catch
        {
            return HResult.E_NOTIMPL;
        }
    }

    public int SetSite(nint site)
    {
        if (site != 0) Marshal.AddRef(site);
        if (_site != 0) Marshal.Release(_site);
        _site = site;
        return HResult.S_OK;
    }

    public int GetSite(in Guid riid, out nint site)
    {
        site = 0;
        if (_site == 0) return HResult.E_FAIL;
        return Marshal.QueryInterface(_site, in riid, out site);
    }

    /// <summary>The selected items with the verb key each one reads, or the open folder for a background entry.
    /// Empty when the selection holds an item the source has no key for.</summary>
    private IReadOnlyList<VerbTarget> Targets(IShellItemArray? items)
    {
        var paths = ShellItems.Paths(items);
        if (_source.Background && paths.Count == 0)
        {
            var folder = _folderFromParent ?? SiteFolder.PathOf(_site);
            paths = folder is null ? [] : [folder];
        }

        var targets = new List<VerbTarget>(paths.Count);
        foreach (var path in paths)
        {
            var key = _source.KeyFor(path);
            if (key is null) return [];
            targets.Add(new VerbTarget(path, key));
        }

        if (targets.Count > 0) _lastTargets = targets;
        return targets;
    }

    private static VerbTarget? FirstOf(IReadOnlyList<VerbTarget> targets) => targets.Count > 0 ? targets[0] : null;

    private VerbTarget DefaultTarget() => new(string.Empty, _source.KeyFor(string.Empty) ?? string.Empty);

    private bool IsVisible(IReadOnlyList<VerbTarget> targets)
    {
        if (targets.Count == 0) return false;
        foreach (var target in targets)
        {
            if (!ReadVerbKey(target, _ => true)) return false;
        }

        if (_subVerb is null)
        {
            // A cascade whose sub verbs are all filtered out would open empty.
            return !ReadVerbKey(targets[0], key => key.GetValue("SubCommands") is not null) || SubVerbNames(targets).Count > 0;
        }

        return _subVerb != UnblockVerb || targets.Any(t => Directory.Exists(t.Path) || MarkOfTheWeb.IsBlocked(t.Path));
    }

    /// <summary>The sub verbs every target's cascade has, in key name order.</summary>
    private IReadOnlyList<string> SubVerbNames(IReadOnlyList<VerbTarget> targets)
    {
        if (targets.Count == 0) return [];
        IEnumerable<string>? common = null;
        foreach (var key in targets.Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var subVerbs = RegistryPath.Open($@"{key}\{SubVerbsKeyName}");
            var names = subVerbs?.GetSubKeyNames() ?? [];
            common = common is null ? names : common.Intersect(names, StringComparer.OrdinalIgnoreCase);
        }

        var ordered = (common ?? []).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.All(t => !Directory.Exists(t.Path)) && !targets.Any(t => MarkOfTheWeb.IsBlocked(t.Path)))
        {
            ordered.RemoveAll(n => n == UnblockVerb);
        }

        return ordered;
    }

    /// <summary>Runs <paramref name="read"/> on this entry's key for <paramref name="target"/> (the verb, or its sub verb).</summary>
    private T? ReadVerbKey<T>(VerbTarget? target, Func<RegistryKey, T?> read)
    {
        if (target is null || string.IsNullOrEmpty(target.Key)) return default;
        var path = _subVerb is null ? target.Key : $@"{target.Key}\{SubVerbsKeyName}\{_subVerb}";
        using var key = RegistryPath.Open(path);
        return key is null ? default : read(key);
    }

    /// <summary>A menu text with its accelerator marks taken out: "&amp;&amp;" is a literal ampersand.</summary>
    private static string? MenuText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const string Literal = "\u0001";
        return text.Replace("&&", Literal, StringComparison.Ordinal)
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace(Literal, "&", StringComparison.Ordinal);
    }

    /// <summary>A registry Icon value ("path", "path,index", quoted or not) as the "path,index" Explorer wants.</summary>
    private static string? IconResource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var index = "0";
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text.AsSpan(comma + 1).Trim(), out var parsed))
        {
            index = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text = text[..comma];
        }

        text = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        return $"{text},{index}";
    }

    /// <summary>The command line of a verb key (its command sub key's default value), or null.</summary>
    private static string? ReadCommand(RegistryKey verbKey)
    {
        using var commandKey = verbKey.OpenSubKey(CommandKeyName);
        return commandKey?.GetValue(null) as string;
    }

    /// <summary>A command line split into its program and its arguments, as CreateProcess reads it.</summary>
    private static (string Exe, string Arguments)? SplitCommand(string line)
    {
        line = line.Trim();
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            return close < 0 ? null : (line[1..close], line[(close + 1)..].TrimStart());
        }

        var space = line.IndexOf(' ');
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..]);
    }

    /// <summary>The program of a command line, when it names a file that exists (a bare "wt.exe" does not).</summary>
    private static string? CommandExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || SplitCommand(command) is not { } split) return null;
        var exe = Environment.ExpandEnvironmentVariables(split.Exe);
        return Path.IsPathRooted(exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>Runs a registry command line the way Explorer would for one item: %1, %L and %V are its path, %W the
    /// folder it sits in (the open folder itself for a background entry).</summary>
    private static void Launch(string command, string path, bool background)
    {
        // A drive root has no parent: it is its own folder then.
        var folder = background ? path : Path.GetDirectoryName(path) ?? path;
        var line = command.Replace("%1", path, StringComparison.Ordinal)
            .Replace("%L", path, StringComparison.OrdinalIgnoreCase)
            .Replace("%V", path, StringComparison.OrdinalIgnoreCase)
            .Replace("%W", folder, StringComparison.OrdinalIgnoreCase);
        if (SplitCommand(line) is not { } split) return;

        var workingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        using var process = Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(split.Exe), split.Arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        });
    }

    private const int AnyProcess = -1;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>One item the entry acts on and the registry verb key it reads for it.</summary>
internal sealed record VerbTarget(string Path, string Key);

/// <summary>Which registry verb a modern menu entry mirrors, and for which items. Each source has its own CLSID in
/// AppxManifest.xml (<see cref="Exports"/> maps them), because the manifest is what tells a background entry apart.</summary>
internal sealed class VerbSource(bool background, Func<string, string?> keyFor, params VerbSource[] extras)
{
    private const string Classes = @"Software\Classes";

    /// <summary>Where the app writes the other apps' verbs it made a package for (ShellMenuVerbs in the app): one sub
    /// key per package, whose Clsid and BgClsid values name its two entries and whose Files, Folders and Background
    /// values are the full path of the classic verb key it mirrors.</summary>
    private const string VerbsKey = @"Software\AresToys\ShellMenu\Verbs";

    /// <summary>Other verbs shown at the end of this cascade, each with its own visibility.</summary>
    public IReadOnlyList<VerbSource> Extras { get; } = extras;

    /// <summary>True for a Directory\Background entry: it acts on the open folder, taken from the site.</summary>
    public bool Background { get; } = background;

    /// <summary>True for another app's verb: with no Icon value it shows its program's icon.</summary>
    public bool IconFromCommand { get; private init; }

    /// <summary>The verb key for one item (under HKCU, or with an HKLM\ or HKCU\ prefix, see
    /// <see cref="RegistryPath"/>), or null when the verb does not apply to it.</summary>
    public string? KeyFor(string path) => keyFor(path);

    /// <summary>The other app's verb whose package names <paramref name="clsid"/> (its files and folders entry, or its
    /// folder background one), or null when no package of ours does.</summary>
    public static VerbSource? ForClsid(Guid clsid)
    {
        using var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey);
        if (verbs is null) return null;
        foreach (var id in verbs.GetSubKeyNames())
        {
            using var verb = verbs.OpenSubKey(id);
            if (Guid.TryParse(verb?.GetValue("Clsid") as string, out var items) && items == clsid)
            {
                return new(false, path => VerbKey(id, Directory.Exists(path) ? "Folders" : "Files")) { IconFromCommand = true };
            }

            if (Guid.TryParse(verb?.GetValue("BgClsid") as string, out var background) && background == clsid)
            {
                return new(true, _ => VerbKey(id, "Background")) { IconFromCommand = true };
            }
        }

        return null;
    }

    /// <summary>The verb key a package mirrors for one item kind, read when the menu opens; null hides the entry. The
    /// path names the original key in its own hive, never HKCR: AresToys hides an HKLM original behind an HKCU key of
    /// the same name, and HKCR would show that empty key (gotchas#hkcr-shadow-hides-hklm-verb).</summary>
    private static string? VerbKey(string id, string kind)
    {
        using var verb = Registry.CurrentUser.OpenSubKey($@"{VerbsKey}\{id}");
        return verb?.GetValue(kind) is string path && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    /// <summary>Image Resizer (ResizerContextMenuRegistration): only when every selected item is an image it lists.</summary>
    public static VerbSource Resize { get; } = new(false, path =>
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) || Directory.Exists(path)
            ? null
            : $@"{Classes}\SystemFileAssociations\{extension.ToLowerInvariant()}\shell\AresToysResize";
    });

    /// <summary>Upload with AresToys (ExplorerContextMenuRegistration), on files.</summary>
    public static VerbSource Upload { get; } = new(false,
        path => Directory.Exists(path) ? null : $@"{Classes}\*\shell\AresToys");

    /// <summary>The AresToys cascade on files and folders (AresToysSubmenuRegistration), with Image Resizer and
    /// Upload at its end. Declared after them: a static initializer reads them.</summary>
    public static VerbSource AresToysItems { get; } = new(false,
        path => Directory.Exists(path) ? $@"{Classes}\Directory\shell\AresToys.Menu" : $@"{Classes}\*\shell\AresToys.Menu",
        Resize, Upload);

    /// <summary>The AresToys cascade on the background of an open folder.</summary>
    public static VerbSource AresToysBackground { get; } = new(true, _ => $@"{Classes}\Directory\Background\shell\AresToys.Menu");

    /// <summary>New+ (NewPlusContextMenuRegistration): the only entry of its own package (Package\NewPlus), so Windows
    /// shows it at the top level of the menu as PowerToys shows New+, not inside AresToys.</summary>
    public static VerbSource NewPlus { get; } = new(true, _ => $@"{Classes}\Directory\Background\shell\AresToysNewPlus");
}

/// <summary>Opens a registry key path read only: "HKLM\..." in HKEY_LOCAL_MACHINE, "HKCU\..." or a bare path in
/// HKEY_CURRENT_USER.</summary>
internal static class RegistryPath
{
    public static RegistryKey? Open(string path)
    {
        if (StripHive(path, "HKLM\\") is { } machine) return Registry.LocalMachine.OpenSubKey(machine);
        if (StripHive(path, "HKEY_LOCAL_MACHINE\\") is { } machineLong) return Registry.LocalMachine.OpenSubKey(machineLong);
        if (StripHive(path, "HKCU\\") is { } user) return Registry.CurrentUser.OpenSubKey(user);
        if (StripHive(path, "HKEY_CURRENT_USER\\") is { } userLong) return Registry.CurrentUser.OpenSubKey(userLong);
        return Registry.CurrentUser.OpenSubKey(path);
    }

    private static string? StripHive(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
}

/// <summary>Resolves a "@dll,-id" menu text (another app's MUIVerb) to its string, as Explorer does.</summary>
internal static unsafe class IndirectString
{
    public static string? Resolve(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '@') return text;
        const int Size = 512;
        var buffer = stackalloc char[Size];
        return SHLoadIndirectString(text, buffer, Size, 0) >= 0 ? new string(buffer) : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, char* outBuffer, int outBufferSize, nint reserved);
}

/// <summary>The enumerator Explorer walks to list a cascade's sub commands.</summary>
[GeneratedComClass]
internal sealed unsafe partial class ExplorerCommandEnumerator(IReadOnlyList<IExplorerCommand> list, int position = 0)
    : IEnumExplorerCommand
{
    private int _position = position;

    public int Next(uint count, nint* commands, uint* fetched)
    {
        if (commands is null) return HResult.E_POINTER;
        uint taken = 0;
        while (taken < count && _position < list.Count)
        {
            commands[taken] = (nint)ComInterfaceMarshaller<IExplorerCommand>.ConvertToUnmanaged(list[_position]);
            taken++;
            _position++;
        }

        if (fetched is not null) *fetched = taken;
        return taken == count ? HResult.S_OK : HResult.S_FALSE;
    }

    public int Skip(uint count)
    {
        _position = Math.Min(list.Count, _position + (int)count);
        return _position < list.Count ? HResult.S_OK : HResult.S_FALSE;
    }

    public int Reset()
    {
        _position = 0;
        return HResult.S_OK;
    }

    public int Clone(out IEnumExplorerCommand? enumerator)
    {
        enumerator = new ExplorerCommandEnumerator(list, _position);
        return HResult.S_OK;
    }
}

/// <summary>Reads the file system paths out of the selection Explorer passes.</summary>
internal static class ShellItems
{
    public static IReadOnlyList<string> Paths(IShellItemArray? items)
    {
        if (items is null || items.GetCount(out var count) < 0) return [];
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) < 0 || item is null) continue;
            if (item.GetDisplayName(Sigdn.FileSysPath, out var name) < 0 || name == 0) continue;
            try
            {
                var path = Marshal.PtrToStringUni(name);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        return paths;
    }
}

/// <summary>The folder an Explorer view shows, from the site it gives a background entry: site, IServiceProvider,
/// IFolderView, its folder as an IShellItem (PowerToys New+ get_path_from_unknown_site does the same).</summary>
internal static unsafe class SiteFolder
{
    private static readonly Guid ServiceProviderIid = new("6d5140c1-7436-11ce-8034-00aa006009fa");
    private static readonly Guid FolderViewIid = new("cde725b0-ccc9-4519-917e-325d72fab4ce");
    private static readonly Guid ShellItemIid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // Vtable slots past IUnknown's three: IServiceProvider.QueryService, IFolderView.GetFolder, IShellItem.GetDisplayName.
    private const int QueryServiceSlot = 3;
    private const int GetFolderSlot = 5;
    private const int GetDisplayNameSlot = 5;

    public static string? PathOf(nint site)
    {
        if (site == 0) return null;
        nint provider = 0, view = 0, item = 0, name = 0;
        try
        {
            if (Marshal.QueryInterface(site, in ServiceProviderIid, out provider) < 0 || provider == 0) return null;
            var service = FolderViewIid;
            var viewIid = FolderViewIid;
            var queryService = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)(*(nint**)provider)[QueryServiceSlot];
            if (queryService(provider, &service, &viewIid, &view) < 0 || view == 0) return null;
            var itemIid = ShellItemIid;
            var getFolder = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)view)[GetFolderSlot];
            if (getFolder(view, &itemIid, &item) < 0 || item == 0) return null;
            var getDisplayName = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)item)[GetDisplayNameSlot];
            if (getDisplayName(item, Sigdn.FileSysPath, &name) < 0 || name == 0) return null;
            return Marshal.PtrToStringUni(name);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (name != 0) Marshal.FreeCoTaskMem(name);
            if (item != 0) Marshal.Release(item);
            if (view != 0) Marshal.Release(view);
            if (provider != 0) Marshal.Release(provider);
        }
    }
}
