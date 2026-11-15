using System.IO;
using System.Runtime.InteropServices;

namespace AresToys.App.Services;

/// <summary>
/// The Mark of the Web: the <c>Zone.Identifier</c> NTFS stream Windows writes on a downloaded file. While it is there
/// Explorer refuses to preview the file and Office opens it in Protected View. This is the one place AresToys reads or
/// removes that stream, for the Explorer "Unblock" verb (<see cref="UnblockContextMenuRegistration"/>) and for Peek.
/// Removing the stream is what the Unblock box in the file's Properties does.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>True when the file at <paramref name="path"/> carries the Mark of the Web.</summary>
    public static bool IsBlocked(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return File.Exists(path + StreamSuffix);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the Mark of the Web from one file. True when the file is unblocked afterwards (also when it never
    /// was blocked); false when the stream could not be removed (read only file, no rights, not NTFS).</summary>
    public static bool Unblock(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (DeleteFile(path + StreamSuffix)) return true;
        var error = Marshal.GetLastWin32Error();
        return error is ErrorFileNotFound or ErrorPathNotFound;
    }

    /// <summary>Unblocks a file, or every file below a folder (linked folders are not entered). Errors on single
    /// files are counted, never thrown.</summary>
    public static UnblockResult UnblockPath(string path)
    {
        if (File.Exists(path))
        {
            var wasBlocked = IsBlocked(path);
            if (!wasBlocked) return new UnblockResult(0, 0);
            return Unblock(path) ? new UnblockResult(1, 0) : new UnblockResult(0, 1);
        }

        if (!Directory.Exists(path)) return new UnblockResult(0, 0);

        var unblocked = 0;
        var failed = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Hidden and system files too (the default skips them). Cloud files are reparse points, so they stay in;
            // the enumeration never recurses into a linked folder by itself.
            AttributesToSkip = 0,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", options);
        }
        catch
        {
            return new UnblockResult(0, 1);
        }

        foreach (var file in files)
        {
            if (!IsBlocked(file)) continue;
            if (Unblock(file)) unblocked++;
            else failed++;
        }

        return new UnblockResult(unblocked, failed);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string path);
}

/// <summary>How many files an unblock freed and how many it could not.</summary>
public readonly record struct UnblockResult(int Unblocked, int Failed);
