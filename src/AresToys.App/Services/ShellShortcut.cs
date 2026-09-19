using System.IO;
using System.Runtime.InteropServices;

namespace AresToys.App.Services;

/// <summary>What a <c>.lnk</c> actually holds. <see cref="IconPath"/> is whatever the shortcut
/// declares as its icon location — usually the target itself, occasionally a separate .ico or a
/// resource inside another binary, in which case <see cref="IconIndex"/> selects which one.
/// Paths come back exactly as the shell stored them, which may include environment variables.</summary>
public sealed record ShellShortcutInfo(
    string TargetPath,
    string Arguments,
    string IconPath,
    int IconIndex,
    string WorkingDirectory,
    /// <summary>The SW_* constant the shortcut asks the target to start with — see
    /// <see cref="ShellShortcut.ShowNormal"/> and friends. Shortcuts only ever carry normal /
    /// minimized / maximized; there's no "hidden" on the Explorer properties page.</summary>
    int ShowCommand = ShellShortcut.ShowNormal,
    /// <summary>The "Run as administrator" checkbox from the shortcut's Advanced properties
    /// (the <c>SLDF_RUNAS_USER</c> flag).</summary>
    bool RunAsAdministrator = false);

/// <summary>Reads and writes Windows shortcuts through <c>IShellLinkW</c> + <c>IPersistFile</c> —
/// the same COM pair Explorer uses, no third-party dependency. Shared because two features need
/// it: the wormholes' right-drag "Create shortcut here", and the launcher, which has to walk a
/// dropped shortcut back to its real target (issue #12) instead of storing a cell that points at
/// the .lnk.</summary>
public static class ShellShortcut
{
    /// <summary>MAX_PATH in wide chars. IShellLinkW's getters take a caller-allocated buffer;
    /// arguments can legitimately run past MAX_PATH so those get a roomier one.</summary>
    private const int MaxPath = 260;
    private const int MaxArguments = 1024;

    /// <summary>SW_SHOWNORMAL — the shortcut wants a regular window.</summary>
    public const int ShowNormal = 1;
    /// <summary>SW_SHOWMAXIMIZED.</summary>
    public const int ShowMaximized = 3;
    /// <summary>SW_SHOWMINNOACTIVE — what Explorer's "Minimized" option writes.</summary>
    public const int ShowMinimized = 7;

    /// <summary>SLDF_RUNAS_USER — the "Run as administrator" bit in the shortcut's flags.</summary>
    private const uint SldfRunAsUser = 0x00002000;

    /// <summary>Write a .lnk at <paramref name="shortcutPath"/> pointing at
    /// <paramref name="targetPath"/>. Working directory defaults to the target's folder and the
    /// icon to the target itself — both are Explorer's own defaults when you create a shortcut
    /// by hand. Pass <paramref name="iconPath"/> to override the latter.</summary>
    public static void Create(
        string targetPath,
        string shortcutPath,
        string? arguments = null,
        string? iconPath = null,
        int iconIndex = 0,
        int showCommand = ShowNormal,
        bool runAsAdmin = false)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(targetPath);
            var workingDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            // Index 0 picks the first icon from the icon resource (or the file-type association
            // for non-PE files / folders).
            link.SetIconLocation(iconPath ?? targetPath, iconIndex);
            if (showCommand != ShowNormal) link.SetShowCmd(showCommand);
            // The elevation bit lives in the link's data-list flags, not on IShellLinkW itself.
            // Must be set before Save — it's persisted as part of the same write.
            if (runAsAdmin && link is IShellLinkDataList dataList)
            {
                dataList.GetFlags(out var flags);
                dataList.SetFlags(flags | SldfRunAsUser);
            }
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Read a shortcut's contents. Returns null for anything that isn't a readable .lnk
    /// — wrong extension, missing file, or a corrupt/unparseable one — so callers can fall back
    /// to treating the input path as the target itself.
    ///
    /// Note there's no <c>Resolve()</c> call here: that's the shell's "hunt for a target that
    /// moved" pass, and it can block on network drives or pop UI. We read what the shortcut
    /// stores and let the caller decide what to do when the target no longer exists.</summary>
    public static ShellShortcutInfo? TryRead(string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath)) return null;
        var path = shortcutPath.Trim();
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path)) return null;

        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                return new ShellShortcutInfo(
                    TargetPath: ReadString(buf => link.GetPath(buf, MaxPath, IntPtr.Zero, 0), MaxPath),
                    Arguments: ReadString(buf => link.GetArguments(buf, MaxArguments), MaxArguments),
                    IconPath: ReadIconLocation(link, out var index),
                    IconIndex: index,
                    WorkingDirectory: ReadString(buf => link.GetWorkingDirectory(buf, MaxPath), MaxPath),
                    ShowCommand: ReadShowCommand(link),
                    RunAsAdministrator: ReadRunAsAdministrator(link));
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch
        {
            // Corrupt .lnk, or COM unavailable. Caller treats null as "not a shortcut".
            return null;
        }
    }

    /// <summary>Walk through a .lnk to its target path. Returns the input untouched for anything
    /// that isn't a resolvable shortcut, so it's safe to call on any path. <c>.url</c> files are
    /// deliberately not resolved — ShellExecute launches those through the browser as they are.</summary>
    public static string ResolveTargetPath(string path)
    {
        var info = TryRead(path);
        return string.IsNullOrEmpty(info?.TargetPath) ? path : info.TargetPath;
    }

    /// <summary>Call one of IShellLinkW's getters into a caller-allocated wide buffer and marshal
    /// the result back. The interface writes a null-terminated string and reports nothing about
    /// the length, so an empty result (the common case for "no arguments") comes back as "".</summary>
    private static string ReadString(Action<IntPtr> getter, int capacityChars)
    {
        var buffer = Marshal.AllocHGlobal(capacityChars * sizeof(char));
        try
        {
            // Zero the first char so a getter that writes nothing leaves us an empty string
            // rather than whatever the heap happened to hold.
            Marshal.WriteInt16(buffer, 0);
            getter(buffer);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadIconLocation(IShellLinkW link, out int iconIndex)
    {
        var index = 0;
        var path = ReadString(buf => link.GetIconLocation(buf, MaxPath, out index), MaxPath);
        iconIndex = index;
        return path;
    }

    private static int ReadShowCommand(IShellLinkW link)
    {
        try
        {
            link.GetShowCmd(out var showCmd);
            return showCmd;
        }
        catch
        {
            return ShowNormal;
        }
    }

    /// <summary>Read the elevation bit. Lives on <c>IShellLinkDataList</c>, a second interface on
    /// the same shell-link object; a shell that doesn't hand it over just means "not elevated".</summary>
    private static bool ReadRunAsAdministrator(IShellLinkW link)
    {
        try
        {
            if (link is not IShellLinkDataList dataList) return false;
            dataList.GetFlags(out var flags);
            return (flags & SldfRunAsUser) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ── COM interop ────────────────────────────────────────────────────────────────

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Declared in full vtable order — the slots we never call still have to be present so
        // the ones we do call land on the right function pointers. IntPtr for the string
        // out-buffers keeps the marshalling explicit (see ReadString).
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>Second interface on the shell-link object, holding the "extra data" blocks —
    /// including the flags word where <c>SLDF_RUNAS_USER</c> records the Advanced → "Run as
    /// administrator" checkbox.</summary>
    [ComImport]
    [Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr pDataBlock);
        void CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        void RemoveDataBlock(uint dwSig);
        void GetFlags(out uint pdwFlags);
        void SetFlags(uint dwFlags);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
