using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace AresToys.App.Services.ShellMenu;

/// <summary>
/// The AresToys entries in the Windows 11 right-click menu (the new one, not "Show more options"). That menu only lists
/// IExplorerCommand handlers of apps with a package identity, so AresToys registers two sparse packages built by
/// src/AresToys.ShellMenu, both pointing at the install folder (their ExternalLocation):
/// <code>
/// AresToys.ContextMenu  AresToys.ShellMenu.msix  ExternalLocation = install folder          (AresToys cascade, Resize, Upload)
/// AresToys.NewPlus      AresToys.NewPlus.msix    ExternalLocation = install folder\NewPlus  (New+, its own slot in the menu)
/// </code>
/// The packages are unsigned, and Windows registers an unsigned package only from an elevated process: turning the
/// option on (and an update that ships a newer package version) runs <c>AresToys.exe --register-shellmenu</c> through
/// UAC once. The menu entries mirror the classic registry verbs, so while the packages are registered those verbs get
/// <c>ProgrammaticAccessOnly</c> (<see cref="SyncClassicVerbs"/>): "Show more options" no longer lists them twice, and
/// the DLL can still read them. Off by default; the registered packages are the state. The folder they were registered
/// from is kept in HKCU\Software\AresToys (ShellMenuLocation) only so startup can tell they point somewhere else.
/// </summary>
public static class ShellMenuPackage
{
    /// <summary>The hidden command line verbs <see cref="Program"/> hands to <see cref="RunCommandLine"/>.</summary>
    public const string RegisterArg = "--register-shellmenu";
    public const string UnregisterArg = "--unregister-shellmenu";

    /// <summary>Exit code of the elevated helper when the user declined the UAC prompt.</summary>
    public const int Cancelled = 1223;

    // The publisher of both manifests (src/AresToys.ShellMenu/Package): FindPackagesForUser needs it with the name.
    private const string Publisher = "CN=AresToys, OID.2.25.311729368913984317654407730594956997722=1";
    private const string ClassesRoot = @"Software\Classes";
    private const string HiddenValue = "ProgrammaticAccessOnly";
    private const string LogFileName = "shellmenu-register.log";
    private const string SettingsKey = @"Software\AresToys";
    private const string LocationValue = "ShellMenuLocation";

    private sealed record Part(string Name, string MsixFile, string ExternalSubfolder);

    private static readonly Part[] Parts =
    [
        new("AresToys.ContextMenu", "AresToys.ShellMenu.msix", ""),
        new("AresToys.NewPlus", "AresToys.NewPlus.msix", "NewPlus"),
    ];

    // The classic verbs the packages show in the new menu (see ExplorerCommands.cs in AresToys.ShellMenu).
    private static IEnumerable<string> ClassicVerbKeys()
    {
        yield return @"*\shell\AresToys.Menu";
        yield return @"Directory\shell\AresToys.Menu";
        yield return @"Directory\Background\shell\AresToys.Menu";
        yield return @"Directory\Background\shell\AresToysNewPlus";
        yield return @"*\shell\AresToys";
        foreach (var ext in ResizerContextMenuRegistration.Extensions)
        {
            yield return $@"SystemFileAssociations\{ext}\shell\AresToysResize";
        }
    }

    private static readonly object Gate = new();
    private static bool? _registered;

    /// <summary>True on Windows 11 when the packages ship next to the exe (a dev build without them hides the option).</summary>
    public static bool IsAvailable
        => IsWindows11 && Parts.All(p => File.Exists(MsixPath(p)));

    private static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>True when the main package is registered for this user. Cached; refreshed by every change made here.</summary>
    public static bool IsRegistered
    {
        get
        {
            lock (Gate)
            {
                return _registered ??= IsWindows11 && FindPackage(Parts[0].Name) is not null;
            }
        }
    }

    /// <summary>Handles the hidden verbs in Program.Main, before WPF starts. Returns the exit code, or null when
    /// <paramref name="args"/> is not one of them.</summary>
    public static int? RunCommandLine(string[] args)
    {
        if (args.Length == 0) return null;
        if (string.Equals(args[0], RegisterArg, StringComparison.OrdinalIgnoreCase)) return RegisterHere();
        if (string.Equals(args[0], UnregisterArg, StringComparison.OrdinalIgnoreCase)) return UnregisterHere();
        return null;
    }

    /// <summary>Registers both packages (UAC once unless AresToys already runs elevated) and hides the classic
    /// duplicates. Returns null on success, an empty string when the user declined the UAC prompt (the caller stays
    /// quiet), and the reason for any other failure.</summary>
    public static async Task<string?> EnableAsync()
    {
        var code = Environment.IsPrivilegedProcess
            ? await Task.Run(RegisterHere).ConfigureAwait(false)
            : await RunElevatedAsync(RegisterArg).ConfigureAwait(false);
        Refresh();
        if (code == 0) return null;
        return code == Cancelled ? string.Empty : ReadLog() ?? $"0x{code:X8}";
    }

    /// <summary>Removes both packages and shows the classic verbs again. Removing a package of this user needs no
    /// admin rights; if Windows refuses anyway, it retries through UAC. Same return contract as <see cref="EnableAsync"/>.</summary>
    public static async Task<string?> DisableAsync()
    {
        var code = await Task.Run(UnregisterHere).ConfigureAwait(false);
        if (code != 0 && !Environment.IsPrivilegedProcess)
        {
            code = await RunElevatedAsync(UnregisterArg).ConfigureAwait(false);
        }

        Refresh();
        if (code == 0) return null;
        return code == Cancelled ? string.Empty : ReadLog() ?? $"0x{code:X8}";
    }

    /// <summary>At startup: when the packages are registered but the install folder ships a newer version (an
    /// update changed the manifest), registers again (one UAC prompt). Nothing happens while the option is off,
    /// below Windows 11 or when the packages are not next to the exe. Never throws.</summary>
    public static async Task<string?> EnsureCurrentAsync()
    {
        try
        {
            var stale = await Task.Run(() =>
            {
                Refresh();
                if (!IsAvailable || !IsRegistered) return false;
                return !RegisteredHere() || Parts.Any(p => FindPackage(p.Name) is not { } registered
                    || ManifestVersion(MsixPath(p)) is { } shipped && shipped > registered);
            }).ConfigureAwait(false);
            if (!stale) return null;
            return await EnableAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Velopack's uninstall hook: removes the packages (no UAC, the hook must be fast) and the hiding
    /// values. Never throws.</summary>
    public static void RemoveForUninstall()
    {
        try
        {
            if (IsWindows11) UnregisterHere();
        }
        catch { /* best effort: the uninstall goes on */ }
    }

    /// <summary>Sets <c>ProgrammaticAccessOnly</c> on every classic verb the packages show while they are registered,
    /// and removes it otherwise. Called by <see cref="ExplorerContextMenuRegistration.NotifyShell"/>, which every
    /// classic verb writer calls after writing, so a rewritten verb is hidden again at once. Best effort.</summary>
    public static void SyncClassicVerbs()
    {
        bool hide;
        try { hide = IsRegistered; }
        catch { return; }
        SetClassicVerbsHidden(hide);
    }

    private static void SetClassicVerbsHidden(bool hide)
    {
        foreach (var path in ClassicVerbKeys())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{path}", writable: true);
                if (key is null) continue;
                if (hide)
                {
                    if (key.GetValue(HiddenValue) is null) key.SetValue(HiddenValue, string.Empty);
                }
                else
                {
                    key.DeleteValue(HiddenValue, throwOnMissingValue: false);
                }
            }
            catch { /* a verb we cannot touch keeps its state */ }
        }
    }

    private static void Refresh()
    {
        lock (Gate)
        {
            _registered = null;
        }

        ExplorerContextMenuRegistration.NotifyShell();
    }

    private static int RegisterHere()
    {
        try
        {
            if (!IsWindows11) return Fail("Windows 11 is required.", 1);
            var manager = new PackageManager();
            foreach (var part in Parts)
            {
                if (!File.Exists(MsixPath(part))) return Fail($"Missing {part.MsixFile} next to AresToys.exe.", 2);
            }

            foreach (var part in Parts)
            {
                if (FindPackage(part.Name) is not null && Remove(manager, part.Name) is { } error) return Fail(error, 8);
            }

            foreach (var part in Parts)
            {
                var options = new AddPackageOptions
                {
                    ExternalLocationUri = new Uri(Path.Combine(InstallFolder, part.ExternalSubfolder)),
                    AllowUnsigned = true,
                };
                var result = manager.AddPackageByUriAsync(new Uri(MsixPath(part)), options).AsTask().GetAwaiter().GetResult();
                if (result.ExtendedErrorCode is { } error)
                {
                    return Fail($"{part.Name}: {result.ErrorText} (0x{error.HResult:X8})", error.HResult);
                }
            }

            SetRegisteredLocation(InstallFolder);
            WriteLog(null);
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex.HResult == 0 ? 3 : ex.HResult);
        }
    }

    private static int UnregisterHere()
    {
        try
        {
            var manager = new PackageManager();
            foreach (var part in Parts)
            {
                var error = Remove(manager, part.Name);
                if (error is not null) return Fail(error, 4);
            }

            lock (Gate)
            {
                _registered = false;
            }

            SetClassicVerbsHidden(false);
            SetRegisteredLocation(null);
            WriteLog(null);
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex.HResult == 0 ? 5 : ex.HResult);
        }
    }

    /// <summary>Removes every registered version of <paramref name="name"/> for this user; the error text, or null.</summary>
    private static string? Remove(PackageManager manager, string name)
    {
        foreach (var package in manager.FindPackagesForUser(string.Empty, name, Publisher))
        {
            var result = manager.RemovePackageAsync(package.Id.FullName).AsTask().GetAwaiter().GetResult();
            if (result.ExtendedErrorCode is { } error)
            {
                return $"{name}: {result.ErrorText} (0x{error.HResult:X8})";
            }
        }

        return null;
    }

    /// <summary>The highest registered version of <paramref name="name"/> for this user, or null.</summary>
    private static Version? FindPackage(string name)
    {
        Version? best = null;
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, name, Publisher))
        {
            var v = package.Id.Version;
            var version = new Version(v.Major, v.Minor, v.Build, v.Revision);
            if (best is null || version > best) best = version;
        }

        return best;
    }

    /// <summary>The Identity Version inside an .msix (a zip with AppxManifest.xml at its root), or null.</summary>
    private static Version? ManifestVersion(string msix)
    {
        try
        {
            using var zip = ZipFile.OpenRead(msix);
            var entry = zip.GetEntry("AppxManifest.xml");
            if (entry is null) return null;
            using var stream = entry.Open();
            var identity = XDocument.Load(stream).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
            return Version.TryParse(identity?.Attribute("Version")?.Value, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int> RunElevatedAsync(string arg)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return 6;
        var info = new ProcessStartInfo(exe, arg) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return 7;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return Cancelled;
        }
    }

    private static string InstallFolder => AppContext.BaseDirectory;

    // True when the packages were registered from this folder. False for another folder (a test copy, a portable
    // extract) and for packages registered by hand before the value existed: startup then registers them again here.
    private static bool RegisteredHere()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(LocationValue) is string location
            && string.Equals(Path.TrimEndingDirectorySeparator(location), Path.TrimEndingDirectorySeparator(InstallFolder),
                StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRegisteredLocation(string? folder)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (folder is null) key.DeleteValue(LocationValue, throwOnMissingValue: false);
            else key.SetValue(LocationValue, folder);
        }
        catch { /* best effort: at worst startup registers the packages once more */ }
    }

    private static string MsixPath(Part part) => Path.Combine(InstallFolder, part.MsixFile);

    // The elevated helper runs as the same user, so both processes share this file: it carries the reason of a
    // failure back to the toggle, which only sees the exit code.
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AresToys", LogFileName);

    private static int Fail(string message, int code)
    {
        WriteLog(message);
        return code;
    }

    private static void WriteLog(string? message)
    {
        try
        {
            if (message is null)
            {
                File.Delete(LogPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, message);
        }
        catch { /* the exit code still tells success from failure */ }
    }

    private static string? ReadLog()
    {
        try { return File.Exists(LogPath) ? File.ReadAllText(LogPath).Trim() : null; }
        catch { return null; }
    }
}
