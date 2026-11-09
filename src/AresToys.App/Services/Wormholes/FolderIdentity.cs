using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AresToys.App.Services.Wormholes;

/// <summary>Identity of a folder that survives being renamed or moved: the NTFS 128-bit file id
/// plus the serial number of the volume it lives on. A rename or a move within the same volume
/// keeps both, so a wormhole can find its source folder again without the user pointing at it —
/// even when the move happened while AresToys wasn't running.</summary>
public readonly record struct FolderIdentity(ulong VolumeSerialNumber, ulong FileIdHigh, ulong FileIdLow)
{
    public bool IsValid => VolumeSerialNumber != 0 && (FileIdHigh != 0 || FileIdLow != 0);

    /// <summary>Persisted form of the file id: a fixed 32-char hex string, high half first.
    /// A string rather than two numbers so the JSON stays readable and can't lose precision
    /// through a round-trip that treats it as a double.</summary>
    public string ToFileIdString() => FileIdHigh.ToString("X16", CultureInfo.InvariantCulture)
                                    + FileIdLow.ToString("X16", CultureInfo.InvariantCulture);

    public static bool TryParseFileId(string? text, out ulong high, out ulong low)
    {
        high = 0;
        low = 0;
        if (text is not { Length: 32 }) return false;
        return ulong.TryParse(text.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out high)
            && ulong.TryParse(text.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out low);
    }
}

/// <summary>Reads and resolves <see cref="FolderIdentity"/> values through the Win32 file-id APIs.
///
/// Works on NTFS and ReFS. On any other file system (FAT/exFAT on a USB stick, some network
/// shares) the ids either aren't stable or the APIs refuse — every method then returns null and
/// callers fall back to asking the user, which is the behaviour that existed before.</summary>
public static class FolderIdentityResolver
{
    /// <summary>Read the identity of an existing folder. Null when the folder is gone or the
    /// volume doesn't support file ids.</summary>
    public static FolderIdentity? Capture(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        try
        {
            using var handle = OpenDirectory(folderPath);
            if (handle is null || handle.IsInvalid) return null;
            return ReadIdentity(handle);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Find where a folder lives now, given the identity captured earlier. Returns null
    /// when the volume isn't mounted, the folder was deleted, or the id can't be resolved (e.g.
    /// the folder was moved to a DIFFERENT volume, which assigns it a brand-new id).</summary>
    public static string? ResolvePath(FolderIdentity identity)
    {
        if (!identity.IsValid) return null;
        try
        {
            using var volumeHint = OpenVolumeWithSerial(identity.VolumeSerialNumber);
            if (volumeHint is null || volumeHint.IsInvalid) return null;

            var descriptor = new FILE_ID_DESCRIPTOR
            {
                dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                Type = FileIdType_ExtendedFileId,
                FileIdLow = identity.FileIdLow,
                FileIdHigh = identity.FileIdHigh,
            };

            using var found = OpenFileById(
                volumeHint, ref descriptor,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                FILE_FLAG_BACKUP_SEMANTICS);
            if (found.IsInvalid) return null;

            var path = GetFinalPath(found);
            if (path is null) return null;
            // Only hand back something that's actually a folder we can use.
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True when <paramref name="folderPath"/> is the folder <paramref name="identity"/>
    /// describes. Used to notice that a path still exists but now points at something else (the
    /// user recreated a folder with the same name, a backup was restored over it).</summary>
    public static bool Matches(string? folderPath, FolderIdentity identity)
    {
        if (!identity.IsValid) return false;
        var current = Capture(folderPath);
        return current is { } c && c == identity;
    }

    private static FolderIdentity? ReadIdentity(SafeFileHandle handle)
    {
        var info = default(FILE_ID_INFO);
        if (!GetFileInformationByHandleEx(handle, FileInformationClass_FileIdInfo, ref info, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
            return null;
        var identity = new FolderIdentity(info.VolumeSerialNumber, info.FileIdHigh, info.FileIdLow);
        return identity.IsValid ? identity : null;
    }

    /// <summary>OpenFileById needs a handle to ANY file on the target volume. We find the volume
    /// by comparing serial numbers of every mounted drive's root.</summary>
    private static SafeFileHandle? OpenVolumeWithSerial(ulong serial)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            SafeFileHandle? root = null;
            try
            {
                if (!drive.IsReady) continue;
                root = OpenDirectory(drive.RootDirectory.FullName);
                if (root is null || root.IsInvalid) { root?.Dispose(); continue; }
                if (ReadIdentity(root) is { } id && id.VolumeSerialNumber == serial) return root;
            }
            catch (Exception) { /* unreadable drive — try the next one */ }
            root?.Dispose();
        }
        return null;
    }

    private static SafeFileHandle? OpenDirectory(string path)
    {
        var handle = CreateFile(
            path,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,   // required to open a DIRECTORY handle
            IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return handle;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (length == 0 || length > buffer.Length) return null;
        var path = new string(buffer, 0, (int)length);
        // The API returns the \\?\ (or \\?\UNC\) prefixed form; normalise back to what the rest
        // of the app stores and shows.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const uint VOLUME_NAME_DOS = 0x0;
    private const int FileInformationClass_FileIdInfo = 18;
    private const uint FileIdType_ExtendedFileId = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;   // FILE_ID_128 as two 64-bit halves
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public uint Type;
        public ulong FileIdLow;   // union member: FILE_ID_128
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, ref FILE_ID_INFO lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint, ref FILE_ID_DESCRIPTOR lpFileId, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}
