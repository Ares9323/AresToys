using System.IO;
using System.Runtime.InteropServices;

namespace AresToys.App.Services.NewPlus;

/// <summary>The few shell calls New+ needs: copy through the shell (progress dialog and Explorer
/// undo, like PowerToys), tell Explorer a path appeared, and read the icon a path shows in
/// Explorer.</summary>
internal static class NewPlusNative
{
    private const uint FO_COPY = 0x0002;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCOPYSECURITYATTRIBS = 0x0800;

    private const uint SHCNE_CREATE = 0x00000002;
    private const uint SHCNE_MKDIR = 0x00000008;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    private const uint SHGFI_ICONLOCATION = 0x1000;
    private const uint ASSOCF_INIT_IGNOREUNKNOWN = 0x00000400;
    private const int ASSOCSTR_DEFAULTICON = 15;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string assoc, string? extra,
        [Out] char[] output, ref uint outputLength);

    /// <summary>Copies a file or folder to <paramref name="destination"/> (the full new path) with
    /// the shell: a progress dialog for big templates and Ctrl+Z in Explorer. Throws when the shell
    /// refuses or the user cancels.</summary>
    public static void CopyWithShell(string source, string destination)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_COPY,
            // SHFileOperation wants both paths to end in two NULs; the marshaller adds one.
            pFrom = source + "\0",
            pTo = destination + "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOF_NOCOPYSECURITYATTRIBS),
            lpszProgressTitle = string.Empty,
        };
        var result = SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted != 0)
            throw new IOException($"The shell could not copy the template (code {result}).");
    }

    /// <summary>Makes Explorer notice a file or folder that was just created.</summary>
    public static void NotifyCreated(string path)
    {
        var kind = Directory.Exists(path) ? SHCNE_MKDIR : SHCNE_CREATE;
        try { SHChangeNotify(kind, SHCNF_PATHW | SHCNF_FLUSH, path, IntPtr.Zero); }
        catch { /* best effort: Explorer refreshes on its own a moment later */ }
    }

    /// <summary>The icon Explorer shows for <paramref name="path"/> as "file,index", the form the
    /// registry <c>Icon</c> value takes. Falls back to the default icon of the extension; null when
    /// neither is known.</summary>
    public static string? IconLocation(string path)
    {
        try
        {
            var info = new SHFILEINFO { szDisplayName = string.Empty, szTypeName = string.Empty };
            SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICONLOCATION);
            if (!string.IsNullOrEmpty(info.szDisplayName))
                return $"{info.szDisplayName},{info.iIcon}";

            var ext = Path.GetExtension(path);
            if (ext.Length == 0) return null;
            var buffer = new char[260];
            var length = (uint)buffer.Length;
            if (AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_DEFAULTICON, ext, null, buffer, ref length) != 0)
                return null;
            var icon = new string(buffer, 0, (int)Math.Max(0, length - 1));
            return icon.Length == 0 ? null : icon;
        }
        catch { return null; }
    }
}
