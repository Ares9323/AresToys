using Microsoft.Win32;

namespace AresToys.App.Services;

/// <summary>
/// Registers / unregisters AresToys as a per-user Explorer context-menu entry on every file
/// (<c>HKCU\Software\Classes\*\shell\AresToys</c>). The verb is "Upload with AresToys" — clicking it
/// runs <c>"arestoys.exe" --upload "&lt;file&gt;"</c>. The primary instance picks the message up via
/// the <see cref="SingleInstanceGuard"/> pipe (prefix <see cref="SingleInstanceGuard.UploadPrefix"/>)
/// and routes the path into <see cref="ManualUploadService.UploadFileAsync(string, System.Threading.CancellationToken)"/>.
///
/// The entry is ALWAYS visible — Win32 shell extensions can't hide a verb based on whether a
/// process is running without a COM-based <c>IExplorerCommand</c> DLL (overkill for AresToys). So
/// we use the ShareX pattern: voice always present, the launched exe figures out the right thing
/// (forward to running primary, or start fresh if cold).
///
/// On Windows 11 the entry shows up under "Show more options" (the legacy menu) — Microsoft
/// hides every third-party verb by default in the new shell menu. There's no workaround short
/// of shipping a packaged Sparse MSIX with an AppExtension manifest, which would defeat the
/// "no installer" charm of the per-user registry approach.
///
/// Per-user (HKCU) means no admin / no UAC; uninstalling AresToys just leaves the keys behind
/// pointing at a now-missing exe — Explorer silently skips them. We expose <see cref="Unregister"/>
/// so the Settings UI's toggle is fully reversible.
/// </summary>
public static class ExplorerContextMenuRegistration
{
    /// <summary>The * wildcard means "any file extension" — matches what ShareX does. Folders use
    /// a separate <c>Directory\shell</c> path which we don't register (uploading a folder isn't
    /// in scope; the user can zip it first).</summary>
    private const string ShellRootPath = @"Software\Classes\*\shell\AresToys";
    private const string CommandRelativePath = @"command";
    private const string MenuLabel = "Upload with AresToys";
    private const string CommandLineArg = "--upload";

    public static bool IsRegistered()
    {
        try
        {
            using var shellKey = Registry.CurrentUser.OpenSubKey(ShellRootPath);
            if (shellKey is null) return false;
            using var cmdKey = shellKey.OpenSubKey(CommandRelativePath);
            var cmd = cmdKey?.GetValue(null) as string;
            // Match on full exe path so a stale registration from another install location reads
            // as "not registered" — caller surfaces "Re-register" instead of false-positive checked.
            return cmd is not null && cmd.Contains(CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Write the registry entries pointing at the current exe + notify the shell so the
    /// menu picks it up without an Explorer restart. Throws on registry failures so the Settings
    /// toggle can surface a clear error message.</summary>
    public static void Register()
    {
        var exe = CurrentExePath();
        using (var shellKey = Registry.CurrentUser.CreateSubKey(ShellRootPath))
        {
            shellKey.SetValue(null, MenuLabel);
            // Icon = the exe itself (Windows pulls the first icon resource). Without this the
            // menu shows a generic verb glyph next to "Upload with AresToys".
            shellKey.SetValue("Icon", exe);
            using var cmdKey = shellKey.CreateSubKey(CommandRelativePath);
            // %1 = the file path Explorer hands us; outer quotes survive paths with spaces.
            cmdKey.SetValue(null, $"\"{exe}\" {CommandLineArg} \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ShellRootPath, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        NotifyShell();
    }

    internal static string CurrentExePath()
        => Environment.ProcessPath
           ?? throw new InvalidOperationException("Couldn't resolve AresToys executable path for context-menu registration.");

    /// <summary>Tell Explorer the file associations changed so a new or removed verb shows up
    /// without restarting it. Shared with <see cref="ResizerContextMenuRegistration"/>.</summary>
    internal static void NotifyShell()
    {
        const uint SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* shell32 missing on bare Windows containers — no-op */ }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
