using Microsoft.Win32;

namespace AresToys.App.Services;

/// <summary>Registers / unregisters AresToys as the handler for <c>.sxie</c> files (ShareX
/// image-effects presets). Mirrors <see cref="SxcuFileAssociation"/> exactly — per-user
/// HKCU\Software\Classes entries, no admin needed, reversible from the same toggle.
/// Keys touched: <c>HKCU\Software\Classes\.sxie</c> (extension → ProgID) +
/// <c>HKCU\Software\Classes\AresToys.sxie\shell\open\command</c> (ProgID → exec line).
///
/// When AresToys ships via Velopack the installer can register both extensions at install
/// time; until then the user opts in from Settings → Image effects.</summary>
public static class SxieFileAssociation
{
    private const string Extension = ".sxie";
    private const string ProgId = "AresToys.sxie";
    private const string ProgIdDescription = "ShareX image effects preset (AresToys)";

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

    public static void Register()
    {
        var exe = CurrentExePath();
        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
        {
            extKey.SetValue(null, ProgId);
        }
        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue(null, ProgIdDescription);
            using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}", writable: true);
            if (extKey?.GetValue(null) as string == ProgId)
            {
                extKey.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort */ }
        NotifyShell();
    }

    private static string CurrentExePath()
        => Environment.ProcessPath
           ?? throw new InvalidOperationException("Couldn't resolve AresToys executable path for file association.");

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
