using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AresToys.App.Services.FileLocks;

/// <summary>What can be done to a lock found by <see cref="LockFinder"/>, as LockHunter offers it: close the handles
/// inside the process (Unlock), end the process, and the file operations that need the lock gone (delete, rename,
/// copy, delete at the next restart). Each returns false or throws instead of guessing; the window reports it.</summary>
public static class LockActions
{
    /// <summary>Closes <paramref name="handle"/> inside process <paramref name="processId"/> (DuplicateHandle with
    /// DUPLICATE_CLOSE_SOURCE). The process is not told: if it writes to the file later it gets an error, which may
    /// make it lose data or crash. False when the process cannot be opened (elevated, another user) or the handle is gone.</summary>
    public static bool CloseRemoteHandle(int processId, nint handle)
    {
        var process = OpenProcess(ProcessDupHandle, false, processId);
        if (process == 0) return false;
        try
        {
            if (!DuplicateHandle(process, handle, GetCurrentProcess(), out var copy, 0, false, DuplicateCloseSource)) return false;
            CloseHandle(copy);
            return true;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Closes every handle of <paramref name="process"/> on the items; modules cannot be closed and are skipped.
    /// Returns how many handles were closed and how many could not be.</summary>
    public static (int Closed, int Failed) Unlock(LockingProcess process)
    {
        int closed = 0, failed = 0;
        foreach (var item in process.Items)
        {
            if (item.IsModule) { failed++; continue; }
            if (CloseRemoteHandle(process.ProcessId, item.Handle)) closed++;
            else failed++;
        }

        return (closed, failed);
    }

    /// <summary>Ends the process. False when it cannot be ended (access denied, already gone).</summary>
    public static bool EndProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException) { return true; } // already gone
        catch (Exception) { return false; }
    }

    /// <summary>Deletes a file, or a folder with everything in it, to the Recycle Bin.</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    /// <summary>Renames the item in its folder to <paramref name="newName"/> (a name, not a path).</summary>
    public static string Rename(string path, string newName)
    {
        var target = Path.Join(Path.GetDirectoryName(path), newName);
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    /// <summary>Copies a file to <paramref name="destination"/> (a full file path).</summary>
    public static void Copy(string path, string destination) => File.Copy(path, destination, overwrite: false);

    /// <summary>Asks Windows to delete the item at the next restart (MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT). Needs
    /// administrator rights: false without them.</summary>
    public static bool DeleteAtRestart(string path)
        => MoveFileEx(path, null, MoveFileDelayUntilReboot);

    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateCloseSource = 1;
    private const uint MoveFileDelayUntilReboot = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? replacement, uint flags);
}
