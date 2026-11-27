using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.App.Services.Launcher;
using Microsoft.Win32;

namespace AresToys.App.Services.ShellMenu;

/// <summary>
/// Other apps' classic verbs ("Open with Code", "WizTree") shown in the Windows 11 menu, each as its own top level
/// entry. Windows folds every entry of one package under the package's name, so each picked verb gets a package of
/// its own, which AresToys writes when the verb is picked, from the template src/AresToys.ShellMenu ships in
/// VerbTemplate\ (Package\Slot\AppxManifest.xml, its logos and a resources.pri made for the name AresToys.V00000000).
/// <code>
/// HKCU\Software\AresToys\ShellMenu\Verbs\&lt;id&gt;  Name, Title, Clsid, BgClsid, Files, Folders, Background (full key paths, hive included)
/// %LOCALAPPDATA%\AresToys\ShellMenu\Verbs\&lt;id&gt;.msix   the package AresToys.V&lt;id&gt;
/// %LOCALAPPDATA%\AresToys\ShellMenu\Verbs\&lt;id&gt;\       its ExternalLocation: DLL copy, Assets (the verb's icon), resources.pri
/// HKCU\Software\AresToys\ShellMenu\Hidden          one value per original verb AresToys hid: its full path = "shadow" | "value"
/// </code>
/// The id is the first 8 hex digits of the SHA-256 of the verb's key name, and the two CLSIDs come from the same
/// name, so a verb keeps its package across updates. The DLL finds its verb by CLSID in the Verbs keys
/// (VerbSource.ForClsid). <see cref="ShellMenuPackage.ApplyVerbsAsync"/> registers and removes the packages (one
/// UAC prompt when something is added) and then keeps a Verbs key only for a registered package. While the packages
/// are registered, a picked verb's original is hidden from "Show more options" so it never shows twice: an HKCU
/// original gets <c>ProgrammaticAccessOnly</c>, an HKLM one an HKCU key of the same path holding only that value,
/// which HKCR shows instead of the machine key (gotchas#hkcr-shadow-hides-hklm-verb). Both are undone when the verb
/// is unpicked or the packages are removed.
/// </summary>
public static partial class ShellMenuVerbs
{
    /// <summary>Every verb package's name starts with this; the 8 hex digit id follows.</summary>
    public const string PackagePrefix = "AresToys.V";

    private const string VerbsKey = @"Software\AresToys\ShellMenu\Verbs";
    private const string HiddenKey = @"Software\AresToys\ShellMenu\Hidden";
    private const string ClassesUser = @"Software\Classes";
    private const string ClassesMachine = @"SOFTWARE\Classes";
    private const string UserPrefix = @"HKCU\";
    private const string MachinePrefix = @"HKLM\";
    private const string HiddenValue = "ProgrammaticAccessOnly";
    private const string Shadow = "shadow";
    private const string Value = "value";
    private const string DllName = "AresToys.ShellMenu.dll";
    private const string ManifestName = "AppxManifest.xml";
    private const string PriName = "resources.pri";
    private const string AssetsName = "Assets";

    // The package name resources.pri in VerbTemplate was made for; same length as every PackagePrefix + id.
    private const string TemplateName = PackagePrefix + "00000000";

    private static readonly (string Kind, string Path)[] Locations =
    [
        ("Files", @"*\shell"),
        ("Folders", @"Directory\shell"),
        // Some apps (Everything) put their folder verb on Folder instead; Directory wins when a verb has both.
        ("Folders", @"Folder\shell"),
        ("Background", @"Directory\Background\shell"),
    ];

    private static readonly string[] Kinds = ["Files", "Folders", "Background"];

    // A verb carrying any of these is not one the classic menu shows plainly (shift only, policy driven, a cascade
    // or a handler the DLL cannot run), so it is not offered.
    private static readonly string[] SkipValues =
    [
        "Extended", "LegacyDisable", "ExplorerCommandHandler", "HideBasedOnVelocityId", "ShowBasedOnVelocityId",
        "SubCommands", "ExtendedSubCommandsKey",
    ];

    // Verbs whose app puts them in the Windows 11 menu itself (ruling: GitAMP does its own), never offered here.
    private static readonly string[] SkipNames = ["AresToys", "GitAMP"];

    /// <summary>One classic verb another app adds, by its key name, with the key it has for each item kind.</summary>
    public sealed record ClassicVerb(string Name, string Title, string? Files, string? Folders, string? Background);

    /// <summary>True when the install folder ships the package template (a dev build without it hides the list).</summary>
    public static bool IsAvailable => File.Exists(Path.Combine(TemplateFolder, ManifestName));

    private static string TemplateFolder => Path.Combine(AppContext.BaseDirectory, "VerbTemplate");

    private static string VerbsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AresToys", "ShellMenu", "Verbs");

    /// <summary>The package of verb <paramref name="id"/>.</summary>
    public static string MsixPath(string id) => Path.Combine(VerbsFolder, id + ".msix");

    /// <summary>The ExternalLocation of verb <paramref name="id"/>'s package.</summary>
    public static string ExternalFolder(string id) => Path.Combine(VerbsFolder, id);

    /// <summary>The id of a package name of ours (AresToys.V1A2B3C4D), or null for any other name.</summary>
    public static string? IdOfPackage(string packageName) =>
        packageName.Length == TemplateName.Length && packageName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
            ? packageName[PackagePrefix.Length..].ToUpperInvariant()
            : null;

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
                    if (SkipNames.Any(skip => name.StartsWith(skip, StringComparison.OrdinalIgnoreCase))) continue;
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

    /// <summary>The key names of the verbs that have a package.</summary>
    public static IReadOnlyList<string> PickedNames()
    {
        var names = new List<string>();
        using var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey);
        if (verbs is null) return names;
        foreach (var id in verbs.GetSubKeyNames())
        {
            using var verb = verbs.OpenSubKey(id);
            if (verb?.GetValue("Name") is string name && !string.IsNullOrEmpty(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>The ids of the verbs with a Verbs key and a package file, which registering the packages adds.</summary>
    public static IReadOnlyList<string> ConfiguredIds()
    {
        using var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey);
        return verbs is null ? [] : verbs.GetSubKeyNames().Where(id => File.Exists(MsixPath(id))).ToList();
    }

    /// <summary>Writes the Verbs key of each verb in <paramref name="picked"/> and its package, when it has none yet.
    /// The keys of the verbs no longer picked stay until <see cref="Prune"/>, so their entries keep working until
    /// their package is gone. Returns the picked ids. Runs on its own STA thread (the icons are drawn with WPF).</summary>
    public static IReadOnlyList<string> Prepare(IReadOnlyList<ClassicVerb> picked)
    {
        IReadOnlyList<string>? ids = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { ids = PrepareHere(picked); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return ids!;
    }

    private static List<string> PrepareHere(IReadOnlyList<ClassicVerb> picked)
    {
        var ids = new List<string>();
        var icons = new IconService();
        foreach (var verb in picked)
        {
            var id = IdOf(verb.Name);
            var clsid = ClsidOf("items", verb.Name);
            var background = ClsidOf("background", verb.Name);
            using (var key = Registry.CurrentUser.CreateSubKey($@"{VerbsKey}\{id}"))
            {
                key.SetValue("Name", verb.Name);
                key.SetValue("Title", verb.Title);
                key.SetValue("Clsid", clsid.ToString("B").ToUpperInvariant());
                key.SetValue("BgClsid", background.ToString("B").ToUpperInvariant());
                foreach (var (kind, path) in new[] { ("Files", verb.Files), ("Folders", verb.Folders), ("Background", verb.Background) })
                {
                    if (path is null) key.DeleteValue(kind, throwOnMissingValue: false);
                    else key.SetValue(kind, path);
                }
            }

            if (!File.Exists(MsixPath(id))) BuildPackage(id, verb, clsid, background, icons);
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>Removes the Verbs key, the package file and the folder of every verb whose package is not in
    /// <paramref name="registeredIds"/>. Best effort: a folder the menu host still holds goes next time.</summary>
    public static void Prune(IReadOnlySet<string> registeredIds)
    {
        // The fixed slots of the first version (commit f082764) kept their picks here.
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\AresToys\ShellMenu\Slots", throwOnMissingSubKey: false);
        using (var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey, writable: true))
        {
            foreach (var id in verbs?.GetSubKeyNames() ?? [])
            {
                if (!registeredIds.Contains(id)) verbs!.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
            }
        }

        if (!Directory.Exists(VerbsFolder)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(VerbsFolder))
        {
            var id = Path.GetFileNameWithoutExtension(entry).ToUpperInvariant();
            if (registeredIds.Contains(id)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch { /* still in use by the menu host */ }
        }
    }

    /// <summary>Copies the install folder's DLL into every verb package's folder where it differs (after an update).
    /// Best effort: a DLL the menu host has loaded stays as it is until the next try.</summary>
    public static void RefreshDlls()
    {
        var source = new FileInfo(Path.Combine(AppContext.BaseDirectory, DllName));
        if (!source.Exists || !Directory.Exists(VerbsFolder)) return;
        foreach (var folder in Directory.EnumerateDirectories(VerbsFolder))
        {
            var target = new FileInfo(Path.Combine(folder, DllName));
            if (target.Exists && target.Length == source.Length && target.LastWriteTimeUtc == source.LastWriteTimeUtc) continue;
            try { source.CopyTo(target.FullName, overwrite: true); }
            catch { /* loaded by the menu host */ }
        }
    }

    /// <summary>Hides the originals of the verbs with a package while <paramref name="packagesRegistered"/>, and shows
    /// again every original AresToys hid that has none any more (all of them when the packages are gone). Best effort.</summary>
    public static void SyncHidden(bool packagesRegistered)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packagesRegistered)
        {
            using var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey);
            foreach (var id in verbs?.GetSubKeyNames() ?? [])
            {
                using var verb = verbs!.OpenSubKey(id);
                if (verb is null) continue;
                foreach (var kind in Kinds)
                {
                    if (verb.GetValue(kind) is string path && !string.IsNullOrEmpty(path)) want.Add(path);
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

    private static string IdOf(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..8];

    private static Guid ClsidOf(string kind, string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{name.ToUpperInvariant()}")).AsSpan(0, 16));

    // ---- the package ----

    /// <summary>Writes <c>&lt;id&gt;.msix</c> and its ExternalLocation folder from the template.</summary>
    private static void BuildPackage(string id, ClassicVerb verb, Guid clsid, Guid background, IconService icons)
    {
        var template = TemplateFolder;
        var external = ExternalFolder(id);
        var assets = Path.Combine(external, AssetsName);
        Directory.CreateDirectory(assets);

        File.Copy(Path.Combine(AppContext.BaseDirectory, DllName), Path.Combine(external, DllName), overwrite: true);
        File.WriteAllBytes(Path.Combine(external, PriName), PatchedPri(Path.Combine(template, PriName), PackagePrefix + id));

        var icon = IconOf(verb);
        foreach (var png in Directory.GetFiles(Path.Combine(template, AssetsName), "*.png"))
        {
            var target = Path.Combine(assets, Path.GetFileName(png));
            if (icon is null || !TryDrawLogo(icons, icon.Value, png, target)) File.Copy(png, target, overwrite: true);
        }

        // The template's comment goes: a title with "--" in it would end the comment early.
        var manifest = Regex.Replace(File.ReadAllText(Path.Combine(template, ManifestName)), "<!--.*?-->", string.Empty, RegexOptions.Singleline)
            .Replace("__ID__", id, StringComparison.Ordinal)
            .Replace("__TITLE__", SecurityElement.Escape(verb.Title), StringComparison.Ordinal)
            .Replace("__BGCLSID__", background.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("__CLSID__", clsid.ToString("D").ToUpperInvariant(), StringComparison.Ordinal);
        WriteMsix(MsixPath(id), manifest, external);
    }

    /// <summary>The template's resources.pri with its package name replaced by <paramref name="name"/>, which has the
    /// same length, so nothing else in the file moves (gotchas#verb-package-pri-name).</summary>
    private static byte[] PatchedPri(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        var from = Encoding.Unicode.GetBytes(TemplateName);
        var to = Encoding.Unicode.GetBytes(name);
        for (var i = bytes.AsSpan().IndexOf(from); i >= 0;)
        {
            to.CopyTo(bytes, i);
            var next = bytes.AsSpan(i + from.Length).IndexOf(from);
            i = next < 0 ? -1 : i + from.Length + next;
        }

        return bytes;
    }

    /// <summary>The icon the classic menu shows for the verb: its Icon value, else its program.</summary>
    private static (string Path, int Index)? IconOf(ClassicVerb verb)
    {
        foreach (var path in new[] { verb.Files, verb.Folders, verb.Background })
        {
            if (path is null) continue;
            using var key = OpenFull(path);
            if (key is null) continue;
            if (key.GetValue("Icon") is string icon && SplitIcon(icon) is { } fromIcon) return fromIcon;
            using var command = key.OpenSubKey("command");
            if (command?.GetValue(null) is string line && CommandExe(line) is { } exe) return (exe, 0);
        }

        return null;
    }

    private static (string Path, int Index)? SplitIcon(string value)
    {
        var text = value.Trim();
        var index = 0;
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text.AsSpan(comma + 1).Trim(), out var parsed))
        {
            index = parsed;
            text = text[..comma];
        }

        text = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        return File.Exists(text) ? (text, index) : null;
    }

    private static string? CommandExe(string line)
    {
        line = line.Trim();
        string exe;
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            if (close < 0) return null;
            exe = line[1..close];
        }
        else
        {
            var space = line.IndexOf(' ');
            exe = space < 0 ? line : line[..space];
        }

        exe = Environment.ExpandEnvironmentVariables(exe);
        return Path.IsPathRooted(exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>Draws the icon in place of one template logo, at the logo's pixel size: the full square for the menu
    /// and taskbar sizes, half of it centred on the 150 tile. False when the icon cannot be read.</summary>
    private static bool TryDrawLogo(IconService icons, (string Path, int Index) icon, string templatePng, string target)
    {
        try
        {
            var size = BitmapDecoder.Create(new Uri(templatePng), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].PixelWidth;
            var inner = Path.GetFileName(templatePng).StartsWith("Square150x150", StringComparison.OrdinalIgnoreCase) ? size / 2 : size;
            var source = icon.Index == 0 ? icons.GetIconAtSize(icon.Path, inner) : icons.GetIcon(icon.Path, icon.Index);
            if (source is null) return false;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var offset = (size - inner) / 2.0;
                context.DrawImage(source, new System.Windows.Rect(offset, offset, inner, inner));
            }

            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(target);
            encoder.Save(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the package with Windows' own packaging API (IAppxFactory): the logos and resources.pri as its
    /// payload, the manifest last. A sparse package carries no DLL; Windows loads it from the ExternalLocation.</summary>
    private static void WriteMsix(string msix, string manifest, string external)
    {
        var temp = msix + ".tmp";
        // Next to the package, not in the ExternalLocation: that folder is what Windows serves as the package's files.
        var manifestFile = msix + ".xml";
        File.WriteAllText(manifestFile, manifest, new UTF8Encoding(false));
        var factory = (IAppxFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781"))!)!;
        IStream? output = null;
        IAppxPackageWriter? writer = null;
        nint hash = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, 0, out var uri));
            hash = Marshal.GetIUnknownForObject(uri);
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(temp, StgmCreate | StgmWrite, FileAttributeNormal, true, null, out output));
            var settings = new AppxPackageSettings { ForceZip32 = 0, HashMethod = hash };
            writer = factory.CreatePackageWriter(output, ref settings);
            foreach (var png in Directory.GetFiles(Path.Combine(external, AssetsName), "*.png"))
            {
                AddPayload(writer, $@"{AssetsName}\{Path.GetFileName(png)}", png, "image/png");
            }

            AddPayload(writer, PriName, Path.Combine(external, PriName), "application/octet-stream");
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(manifestFile, StgmRead, 0, false, null, out var manifestStream));
            try { writer.Close(manifestStream); }
            finally { Marshal.ReleaseComObject(manifestStream); }
        }
        finally
        {
            if (writer is not null) Marshal.ReleaseComObject(writer);
            if (output is not null) Marshal.ReleaseComObject(output);
            if (hash != 0) Marshal.Release(hash);
            Marshal.ReleaseComObject(factory);
            File.Delete(manifestFile);
        }

        File.Move(temp, msix, overwrite: true);
    }

    private static void AddPayload(IAppxPackageWriter writer, string name, string file, string contentType)
    {
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(file, StgmRead, 0, false, null, out var stream));
        try { writer.AddPayloadFile(name, contentType, CompressionNormal, stream); }
        finally { Marshal.ReleaseComObject(stream); }
    }

    private const uint StgmRead = 0;
    private const uint StgmWrite = 0x1;
    private const uint StgmCreate = 0x1000;
    private const uint FileAttributeNormal = 0x80;
    private const int CompressionNormal = 1;

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateUri(string uri, uint flags, nint reserved, [MarshalAs(UnmanagedType.Interface)] out object result);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppxPackageSettings
    {
        public int ForceZip32;
        public nint HashMethod;
    }

    [ComImport, Guid("9099e33b-246f-41e4-881a-008eb613f858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppxPackageWriter
    {
        void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string contentType, int compression, IStream input);
        void Close(IStream manifest);
    }

    [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppxFactory
    {
        IAppxPackageWriter CreatePackageWriter(IStream output, ref AppxPackageSettings settings);
    }

    // ---- the originals in "Show more options" ----

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
    // hid it on purpose, and AresToys must not bring it back.
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

    /// <summary>A full key path (HKCU\... or HKLM\...) opened in its own hive, read only.</summary>
    private static RegistryKey? OpenFull(string path) =>
        Strip(path, UserPrefix) is { } user ? Registry.CurrentUser.OpenSubKey(user)
        : Strip(path, MachinePrefix) is { } machine ? Registry.LocalMachine.OpenSubKey(machine)
        : null;

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
