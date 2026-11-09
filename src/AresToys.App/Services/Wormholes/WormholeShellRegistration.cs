using Microsoft.Win32;

namespace AresToys.App.Services.Wormholes;

/// <summary>Registers / unregisters AresToys as a per-user Explorer folder context-menu entry.
/// Two registry roots, same command shape:
/// <list type="bullet">
///   <item><c>HKCU\Software\Classes\Directory\shell\AresToysCreateWormhole</c> — appears when
///         the user right-clicks a folder ITEM (in a folder view or on the desktop).</item>
///   <item><c>HKCU\Software\Classes\Directory\Background\shell\AresToysCreateWormhole</c> —
///         appears on the BACKGROUND of an open folder window (the "create wormhole for the
///         folder I'm currently looking at" gesture). Uses <c>%V</c> instead of <c>%1</c>
///         because the background variant doesn't receive a clicked item path.</item>
/// </list>
/// Both run <c>"arestoys.exe" --create-wormhole "&lt;folder&gt;"</c>; the running primary
/// instance (or a fresh one) catches the CLI flag and spawns a wormhole record + window.
///
/// Per-user (HKCU) means no admin / no UAC needed. Mirrors the pattern in
/// <see cref="ExplorerContextMenuRegistration"/> for the file-upload verb — same idempotency
/// guarantees, same shell-refresh notification on register/unregister.
///
/// On Windows 11 the entry shows up under "Show more options" (legacy menu); the new modern
/// shell menu hides third-party verbs without a packaged MSIX IExplorerCommand DLL.</summary>
public static class WormholeShellRegistration
{
    private const string ItemShellPath       = @"Software\Classes\Directory\shell\AresToysCreateWormhole";
    private const string BackgroundShellPath = @"Software\Classes\Directory\Background\shell\AresToysCreateWormhole";
    private const string CommandRelativePath = @"command";
    private const string MenuLabel = "Create AresToys Wormhole";
    private const string ItemCommandLineArg = "--create-wormhole";
    /// <summary>Background variant doesn't pass the folder being viewed (we don't want the
    /// desktop to auto-mirror Desktop). The flag with no path opens the New Wormhole dialog
    /// so the user picks the source folder explicitly.</summary>
    private const string BackgroundCommandLineArg = "--new-wormhole-dialog";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ItemShellPath);
            if (key is null) return false;
            using var cmd = key.OpenSubKey(CommandRelativePath);
            var stored = cmd?.GetValue(null) as string;
            return stored is not null && stored.Contains(CurrentExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Register()
    {
        var exe = CurrentExePath();
        // Folder ITEM verb — Explorer passes the clicked folder as %1.
        WriteEntry(ItemShellPath, exe, ItemCommandLineArg, argPlaceholder: "%1");
        // Folder BACKGROUND verb — opens the New Wormhole dialog instead of auto-creating
        // with the current folder (the user almost never wants a Desktop-mirror wormhole when
        // they right-click an empty desktop area).
        WriteEntry(BackgroundShellPath, exe, BackgroundCommandLineArg, argPlaceholder: null);
        NotifyShell();
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ItemShellPath, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        try { Registry.CurrentUser.DeleteSubKeyTree(BackgroundShellPath, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        NotifyShell();
    }

    private static void WriteEntry(string shellPath, string exe, string commandLineArg, string? argPlaceholder)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(shellPath);
        shellKey.SetValue(null, MenuLabel);
        // Icon = the exe itself (Windows pulls the first icon resource). Matches the upload
        // verb so the AresToys menu entries look uniform when both are present.
        shellKey.SetValue("Icon", exe);
        using var cmdKey = shellKey.CreateSubKey(CommandRelativePath);
        // argPlaceholder=null → background variant: no path forwarded, dialog handles selection.
        // argPlaceholder="%1"/"%V" → Item / Background-with-path variants pass the folder path.
        var commandLine = argPlaceholder is null
            ? $"\"{exe}\" {commandLineArg}"
            : $"\"{exe}\" {commandLineArg} \"{argPlaceholder}\"";
        cmdKey.SetValue(null, commandLine);
    }

    private static string CurrentExePath()
        => Environment.ProcessPath
           ?? throw new InvalidOperationException("Couldn't resolve AresToys executable path for shell registration.");

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
