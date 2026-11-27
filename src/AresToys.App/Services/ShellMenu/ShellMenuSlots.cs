using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AresToys.App.Services.ShellMenu;

/// <summary>
/// Other apps' classic verbs ("Open with Code", "Open in GitAMP") shown in the Windows 11 menu, each as its own top
/// level entry: AresToys registers eight slot packages (AresToys.Slot1 to 8, built by src/AresToys.ShellMenu from
/// Package\Slot) with the others, and this class says what each slot shows. The state is the registry:
/// <code>
/// HKCU\Software\AresToys\ShellMenu\Slots\N   Name, Files, Folders, Background (full key paths, hive included)
/// HKCU\Software\AresToys\ShellMenu\Hidden    one value per original verb AresToys hid: its full path = "shadow" | "value"
/// </code>
/// The DLL reads the slot keys each time the menu opens, so picking a verb needs no UAC. While the packages are
/// registered, a picked verb's original is hidden from "Show more options" so it never shows twice: an HKCU original
/// gets <c>ProgrammaticAccessOnly</c>, an HKLM one an HKCU key of the same path holding only that value, which HKCR
/// shows instead of the machine key (gotchas#hkcr-shadow-hides-hklm-verb). Both are undone when the verb is unpicked
/// or the packages are removed.
/// </summary>
public static partial class ShellMenuSlots
{
    /// <summary>The number of slot packages the csproj builds (its _Slot items).</summary>
    public const int Count = 8;

    private const string SlotsKey = @"Software\AresToys\ShellMenu\Slots";
    private const string HiddenKey = @"Software\AresToys\ShellMenu\Hidden";
    private const string ClassesUser = @"Software\Classes";
    private const string ClassesMachine = @"SOFTWARE\Classes";
    private const string UserPrefix = @"HKCU\";
    private const string MachinePrefix = @"HKLM\";
    private const string HiddenValue = "ProgrammaticAccessOnly";
    private const string Shadow = "shadow";
    private const string Value = "value";

    private static readonly (string Kind, string Path)[] Locations =
    [
        ("Files", @"*\shell"),
        ("Folders", @"Directory\shell"),
        // Some apps (Everything) put their folder verb on Folder instead; Directory wins when a verb has both.
        ("Folders", @"Folder\shell"),
        ("Background", @"Directory\Background\shell"),
    ];

    // A verb carrying any of these is not one the classic menu shows plainly (shift only, policy driven, a cascade
    // or a handler the slot DLL cannot run), so it is not offered.
    private static readonly string[] SkipValues =
    [
        "Extended", "LegacyDisable", "ExplorerCommandHandler", "HideBasedOnVelocityId", "ShowBasedOnVelocityId",
        "SubCommands", "ExtendedSubCommandsKey",
    ];

    /// <summary>One classic verb another app adds, by its key name, with the key it has for each item kind.</summary>
    public sealed record ClassicVerb(string Name, string Title, string? Files, string? Folders, string? Background);

    /// <summary>The classic verbs other apps add on files, folders and folder backgrounds, by title. HKCU wins over
    /// HKLM for the same key name and kind, as HKCR does; AresToys' own verbs and the shadow keys it wrote are left out.</summary>
    public static IReadOnlyList<ClassicVerb> FindClassicVerbs()
    {
        var hidden = HiddenOriginals();
        var found = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, location) in Locations)
        {
            foreach (var (prefix, root, classes) in new[] { (UserPrefix, Registry.CurrentUser, ClassesUser), (MachinePrefix, Registry.LocalMachine, ClassesMachine) })
            {
                using var shell = root.OpenSubKey($@"{classes}\{location}");
                if (shell is null) continue;
                foreach (var name in shell.GetSubKeyNames())
                {
                    if (name.StartsWith("AresToys", StringComparison.OrdinalIgnoreCase)) continue;
                    if (found.TryGetValue(name, out var kinds) && kinds.ContainsKey(kind)) continue;
                    var fullPath = $@"{prefix}{classes}\{location}\{name}";
                    using var verb = shell.OpenSubKey(name);
                    if (verb is null || !IsPlainVerb(verb, hidden.ContainsKey(fullPath))) continue;
                    if (prefix == MachinePrefix && IsHiddenByOthers(location, name, hidden.ContainsKey(fullPath))) continue;
                    if (!found.TryGetValue(name, out kinds)) found[name] = kinds = new(StringComparer.OrdinalIgnoreCase);
                    kinds[kind] = fullPath;
                    if (!titles.ContainsKey(name)) titles[name] = Title(verb) ?? name;
                }
            }
        }

        return found
            .Select(pair => new ClassicVerb(pair.Key, titles[pair.Key],
                pair.Value.GetValueOrDefault("Files"), pair.Value.GetValueOrDefault("Folders"), pair.Value.GetValueOrDefault("Background")))
            .OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The key names of the verbs in the slots, slot 1 first.</summary>
    public static IReadOnlyList<string> PickedNames()
    {
        var names = new List<string>();
        for (var n = 1; n <= Count; n++)
        {
            using var slot = Registry.CurrentUser.OpenSubKey($@"{SlotsKey}\{n}");
            if (slot?.GetValue("Name") is string name && !string.IsNullOrEmpty(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>Puts <paramref name="picked"/> (at most <see cref="Count"/>) in the slots, in order, empties the rest,
    /// then hides or shows the originals (<see cref="SyncHidden"/>).</summary>
    public static void SetPicked(IReadOnlyList<ClassicVerb> picked)
    {
        using (var slots = Registry.CurrentUser.CreateSubKey(SlotsKey))
        {
            for (var n = 1; n <= Count; n++)
            {
                slots.DeleteSubKey(n.ToString(System.Globalization.CultureInfo.InvariantCulture), throwOnMissingSubKey: false);
                if (n > picked.Count) continue;
                var verb = picked[n - 1];
                using var slot = slots.CreateSubKey(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                slot.SetValue("Name", verb.Name);
                if (verb.Files is not null) slot.SetValue("Files", verb.Files);
                if (verb.Folders is not null) slot.SetValue("Folders", verb.Folders);
                if (verb.Background is not null) slot.SetValue("Background", verb.Background);
            }
        }

        SyncHidden(ShellMenuPackage.IsRegistered);
    }

    /// <summary>Hides the originals of the slots' verbs while <paramref name="packagesRegistered"/>, and shows again
    /// every original AresToys hid that is no longer in a slot (all of them when the packages are gone). Best effort.</summary>
    public static void SyncHidden(bool packagesRegistered)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packagesRegistered)
        {
            for (var n = 1; n <= Count; n++)
            {
                using var slot = Registry.CurrentUser.OpenSubKey($@"{SlotsKey}\{n}");
                if (slot is null) continue;
                foreach (var (kind, _) in Locations)
                {
                    if (slot.GetValue(kind) is string path && !string.IsNullOrEmpty(path)) want.Add(path);
                }
            }
        }

        var hidden = HiddenOriginals();
        foreach (var (path, how) in hidden)
        {
            if (!want.Contains(path)) Show(path, how);
        }

        foreach (var path in want)
        {
            if (!hidden.ContainsKey(path)) Hide(path);
        }
    }

    private static bool IsPlainVerb(RegistryKey verb, bool hiddenByAresToys)
    {
        foreach (var value in SkipValues)
        {
            if (verb.GetValue(value) is not null) return false;
        }

        if (!hiddenByAresToys && verb.GetValue(HiddenValue) is not null) return false;
        using var command = verb.OpenSubKey("command");
        return command?.GetValue(null) is string line && !string.IsNullOrWhiteSpace(line) && command.GetValue("DelegateExecute") is null;
    }

    // An HKLM verb that an HKCU key of the same path hides, when AresToys did not write that key: something else
    // hid it on purpose, and the slot must not bring it back.
    private static bool IsHiddenByOthers(string location, string name, bool hiddenByAresToys)
    {
        if (hiddenByAresToys) return false;
        using var user = Registry.CurrentUser.OpenSubKey($@"{ClassesUser}\{location}\{name}");
        return user?.GetValue(HiddenValue) is not null;
    }

    private static string? Title(RegistryKey verb)
    {
        var text = Resolve(verb.GetValue("MUIVerb") as string ?? verb.GetValue(null) as string);
        if (string.IsNullOrEmpty(text)) return null;
        const string Literal = "\u0001";
        return text.Replace("&&", Literal, StringComparison.Ordinal)
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace(Literal, "&", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> HiddenOriginals()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(HiddenKey);
        if (key is null) return result;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string how) result[name] = how;
        }

        return result;
    }

    private static void Hide(string path)
    {
        try
        {
            string how;
            if (Strip(path, UserPrefix) is { } userPath)
            {
                using var key = Registry.CurrentUser.OpenSubKey(userPath, writable: true);
                if (key is null || key.GetValue(HiddenValue) is not null) return;
                key.SetValue(HiddenValue, string.Empty);
                how = Value;
            }
            else if (Strip(path, MachinePrefix) is { } machinePath && machinePath.StartsWith(ClassesMachine + @"\", StringComparison.OrdinalIgnoreCase))
            {
                var shadowPath = ClassesUser + machinePath[ClassesMachine.Length..];
                // A real HKCU key of that name is a verb of its own (HKCR already shows it, not the machine one).
                using (var existing = Registry.CurrentUser.OpenSubKey(shadowPath))
                {
                    if (existing is not null) return;
                }

                using var shadow = Registry.CurrentUser.CreateSubKey(shadowPath);
                shadow.SetValue(HiddenValue, string.Empty);
                how = Shadow;
            }
            else
            {
                return;
            }

            using var hidden = Registry.CurrentUser.CreateSubKey(HiddenKey);
            hidden.SetValue(path, how);
        }
        catch { /* a verb we cannot touch stays visible */ }
    }

    private static void Show(string path, string how)
    {
        try
        {
            if (how == Value && Strip(path, UserPrefix) is { } userPath)
            {
                using var key = Registry.CurrentUser.OpenSubKey(userPath, writable: true);
                key?.DeleteValue(HiddenValue, throwOnMissingValue: false);
            }
            else if (how == Shadow && Strip(path, MachinePrefix) is { } machinePath
                && machinePath.StartsWith(ClassesMachine + @"\", StringComparison.OrdinalIgnoreCase))
            {
                var shadowPath = ClassesUser + machinePath[ClassesMachine.Length..];
                // Only the key AresToys wrote: one value, no sub keys. Anything else was added since; leave it.
                using (var shadow = Registry.CurrentUser.OpenSubKey(shadowPath))
                {
                    if (shadow is not null && shadow.SubKeyCount == 0 && shadow.GetValueNames() is [HiddenValue])
                    {
                        shadow.Close();
                        Registry.CurrentUser.DeleteSubKey(shadowPath, throwOnMissingSubKey: false);
                    }
                }
            }

            using var hidden = Registry.CurrentUser.OpenSubKey(HiddenKey, writable: true);
            hidden?.DeleteValue(path, throwOnMissingValue: false);
        }
        catch { /* stays hidden; the next sync tries again */ }
    }

    private static string? Strip(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;

    /// <summary>A "@dll,-id" menu text resolved to its string, as Explorer shows it.</summary>
    private static unsafe string? Resolve(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '@') return text;
        const int Size = 512;
        var buffer = stackalloc char[Size];
        return SHLoadIndirectString(text, buffer, Size, 0) >= 0 ? new string(buffer) : null;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* outBuffer, int outBufferSize, nint reserved);
}
