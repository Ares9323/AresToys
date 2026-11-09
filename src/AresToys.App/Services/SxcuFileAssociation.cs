using Microsoft.Win32;

namespace AresToys.App.Services;

/// <summary>Registers / unregisters AresToys as the handler for <c>.sxcu</c> files via per-user
/// HKCU\Software\Classes entries (no admin needed, no system-wide effect — only the current
/// user's Explorer / shell uses the binding). Reversible from the same toggle. The keys we
/// touch: <c>HKCU\Software\Classes\.sxcu</c> (extension → ProgID) +
/// <c>HKCU\Software\Classes\AresToys.sxcu\shell\open\command</c> (ProgID → exec line).
///
/// When AresToys ships via Velopack (M7) the installer can do this at install time and uninstall
/// it on uninstall; until then the user does it manually from Settings → Uploaders → Custom.
/// We don't auto-register on first run because that's an OS-level change the user should opt
/// into explicitly.</summary>
public static class SxcuFileAssociation
{
    private const string Extension = ".sxcu";
    private const string ProgId = "AresToys.sxcu";
    private const string ProgIdDescription = "ShareX custom uploader (AresToys)";

    /// <summary>True when the per-user registry entries point at the currently-running
    /// AresToys.exe. False when not registered or when registered but pointing at a stale path
    /// (older install, different folder) — caller decides whether to surface that as
    /// "Register" or "Re-register".</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}");
            if (extKey?.GetValue(null) as string != ProgId) return false;
            using var cmdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            var cmd = cmdKey?.GetValue(null) as string;
            return cmd is not null && cmd.Contains(CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Write the registry entries and notify the shell so Explorer picks the change up
    /// without a logoff. Throws on registry failures (caller surfaces to user).</summary>
    public static void Register()
    {
        var exe = CurrentExePath();
        // Extension → ProgID. Empty default value clears any previous binding before we set ours.
        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
        {
            extKey.SetValue(null, ProgId);
        }
        // ProgID → friendly name + open command. "%1" is the file path, quoted so paths with
        // spaces survive the shell's argv parsing.
        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue(null, ProgIdDescription);
            using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        NotifyShell();
    }

    /// <summary>Remove our registry entries. Doesn't restore any previous handler — Windows
    /// falls back to "Open with…" automatically when the ProgID disappears.</summary>
    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}", writable: true);
            if (extKey?.GetValue(null) as string == ProgId)
            {
                // Only clear the value if it's still ours — don't trample a binding the user has
                // since pointed at another app. Empty-string name targets the (Default) value.
                extKey.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort */ }
        NotifyShell();
    }

    /// <summary>Absolute path to the currently-executing AresToys.exe. <see cref="Environment.ProcessPath"/>
    /// is the canonical .NET 6+ way; null only on niche scenarios (single-file-published apps
    /// trimmed to no entry assembly path) we don't ship into.</summary>
    private static string CurrentExePath()
        => Environment.ProcessPath
           ?? throw new InvalidOperationException("Couldn't resolve AresToys executable path for file association.");

    /// <summary>SHChangeNotify with SHCNE_ASSOCCHANGED tells Explorer to drop its cached handler
    /// lookup so the new association takes effect immediately (otherwise the user would need to
    /// log out / log back in, which is unfriendly).</summary>
    private static void NotifyShell()
    {
        const uint SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* shell32 missing on bare Windows containers — no-op */ }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
