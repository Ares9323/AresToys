using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AresToys.App.Services.FileLocks;

/// <summary>One open handle (or a loaded module, <see cref="Handle"/> 0) that keeps <see cref="Path"/> busy.</summary>
public sealed record LockedItem(string Path, nint Handle)
{
    /// <summary>A loaded DLL or exe: no handle to close, only ending the process frees it.</summary>
    public bool IsModule => Handle == 0;
}

/// <summary>A process that holds one or more of the searched items.</summary>
public sealed record LockingProcess(int ProcessId, string Name, string? ExecutablePath, string? User, IReadOnlyList<LockedItem> Items);

/// <summary>Finds the processes that keep files or folders open: every file handle in the system
/// (NtQuerySystemInformation SystemExtendedHandleInformation) whose kernel name is one of the searched items or lies
/// under a searched folder, plus every loaded module under them. Port of PowerToys FileLocksmithLib
/// (find_processes_recursive), improved: only handles of the File object type are looked at (the type index is learnt
/// from a handle of our own, so no per-handle type query), and a handle whose name query hangs (a synchronous pipe) is
/// skipped by abandoning the worker thread after a timeout instead of TerminateThread. Without administrator rights the
/// handles of elevated processes and other users cannot be read: <see cref="LockSearchResult.Partial"/> says so.</summary>
public static class LockFinder
{
    private static readonly TimeSpan HangTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Searches <paramref name="paths"/> (files, or folders searched recursively). Slow (hundreds of ms to a few
    /// seconds): call it off the UI thread.</summary>
    public static LockSearchResult Find(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var targets = new List<(string Kernel, string Path, bool Folder)>();
        foreach (var path in paths)
        {
            var kernel = PathToKernelName(path);
            if (kernel is not null) targets.Add((kernel, path, Directory.Exists(path)));
        }

        if (targets.Count == 0) return new LockSearchResult([], Partial: false);

        string? Match(string kernelName)
        {
            foreach (var (kernel, path, folder) in targets)
            {
                if (string.Equals(kernelName, kernel, StringComparison.OrdinalIgnoreCase)) return path;
                if (!folder) continue;
                var prefix = kernel.EndsWith('\\') ? kernel : kernel + "\\";
                if (kernelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return System.IO.Path.Join(path, kernelName[prefix.Length..]);
            }

            return null;
        }

        var found = new Dictionary<int, List<LockedItem>>();
        void Add(int pid, LockedItem item)
        {
            if (!found.TryGetValue(pid, out var list)) found[pid] = list = new List<LockedItem>();
            if (!list.Contains(item)) list.Add(item);
        }

        var partial = false;
        foreach (var handle in FileHandles(cancellationToken, ref partial))
        {
            if (Match(handle.KernelName) is { } path) Add(handle.ProcessId, new LockedItem(path, handle.Handle));
        }

        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.Id is 0 or 4 || process.Id == self) continue;
                foreach (var module in ModulesOf(process.Id))
                {
                    var kernel = PathToKernelName(module);
                    if (kernel is not null && Match(kernel) is { } path) Add(process.Id, new LockedItem(path, 0));
                }
            }
        }

        var result = new List<LockingProcess>();
        foreach (var (pid, items) in found)
        {
            if (pid == self) continue;
            var (name, exe) = Describe(pid);
            result.Add(new LockingProcess(pid, name, exe, UserOf(pid),
                items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new LockSearchResult(result, partial || !ElevationService.IsCurrentProcessElevated);
    }

    /// <summary>The kernel name of <paramref name="path"/> (\Device\HarddiskVolume3\...), the form handle names come in,
    /// or null when the item cannot be opened at all.</summary>
    public static string? PathToKernelName(string path)
    {
        using var handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid) return null;
        var buffer = Marshal.AllocHGlobal(NameBufferSize);
        try { return KernelNameOf(handle.DangerousGetHandle(), buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ── Handles ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct FileHandle(int ProcessId, nint Handle, string KernelName);

    private readonly record struct Entry(int ProcessId, nint Handle);

    private static List<FileHandle> FileHandles(CancellationToken cancellationToken, ref bool partial)
    {
        var entries = FileHandleEntries();
        var results = new List<FileHandle>(256);
        var processes = new Dictionary<int, nint>();
        var next = 0;
        try
        {
            while (next < entries.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A worker reads names from index `next`; when it stops making progress it is stuck in a name query
                // that never returns (a synchronous pipe): leave it blocked and go on from the handle after it.
                var progress = new Progress { Index = next };
                var worker = new Thread(() => ReadNames(entries, progress, processes, results)) { IsBackground = true, Name = "AresToys lock finder" };
                worker.Start();
                var last = -1;
                while (!worker.Join(HangTimeout))
                {
                    var now = Volatile.Read(ref progress.Index);
                    if (now == last)
                    {
                        progress.Abandoned = true;
                        partial = true;
                        break;
                    }

                    last = now;
                }

                next = Volatile.Read(ref progress.Index) + (progress.Abandoned ? 1 : 0);
                if (!progress.Abandoned) break;
            }
        }
        finally
        {
            lock (processes)
            {
                foreach (var process in processes.Values) CloseHandle(process);
                processes.Clear();
            }
        }

        lock (results) return results.ToList();
    }

    private sealed class Progress
    {
        public int Index;
        public volatile bool Abandoned;
    }

    private static void ReadNames(List<Entry> entries, Progress progress, Dictionary<int, nint> processes, List<FileHandle> results)
    {
        // The thread owns its buffer: an abandoned worker may still be inside NtQueryObject with it.
        var buffer = Marshal.AllocHGlobal(NameBufferSize);
        try { ReadNames(entries, progress, processes, results, buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ReadNames(List<Entry> entries, Progress progress, Dictionary<int, nint> processes, List<FileHandle> results, nint buffer)
    {
        var current = GetCurrentProcess();
        for (var i = progress.Index; i < entries.Count; i++)
        {
            Volatile.Write(ref progress.Index, i);
            if (progress.Abandoned) return;
            var entry = entries[i];
            nint process;
            lock (processes)
            {
                if (!processes.TryGetValue(entry.ProcessId, out process))
                {
                    process = OpenProcess(ProcessDupHandle, false, entry.ProcessId);
                    processes[entry.ProcessId] = process;
                }
            }

            if (process == 0 || progress.Abandoned) continue;
            if (!DuplicateHandle(process, entry.Handle, current, out var copy, 0, false, DuplicateSameAccess)) continue;
            try
            {
                // Only disk files: GetFileType answers for pipes and consoles without touching them.
                if (GetFileType(copy) != FileTypeDisk) continue;
                var name = KernelNameOf(copy, buffer);
                if (name is null || progress.Abandoned) continue;
                lock (results) results.Add(new FileHandle(entry.ProcessId, entry.Handle, name));
            }
            finally { CloseHandle(copy); }
        }

        Volatile.Write(ref progress.Index, entries.Count);
    }

    /// <summary>The handles of File objects in other processes, read from the system handle table.</summary>
    private static List<Entry> FileHandleEntries()
    {
        // Learn the File object type index from a file handle of our own: it differs between Windows builds.
        using var probe = CreateFile(Environment.ProcessPath ?? Environment.SystemDirectory, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        var table = QuerySystemInformation(SystemExtendedHandleInformation);
        try
        {
            var count = (long)Marshal.ReadIntPtr(table.Pointer);
            var entrySize = Marshal.SizeOf<HandleEntry>();
            var first = table.Pointer + 2 * IntPtr.Size;
            var self = Environment.ProcessId;
            var probeHandle = probe.IsInvalid ? 0 : probe.DangerousGetHandle();

            int? fileType = null;
            for (long i = 0; i < count && fileType is null; i++)
            {
                var e = Marshal.PtrToStructure<HandleEntry>(first + (nint)(i * entrySize));
                if ((int)e.UniqueProcessId == self && e.HandleValue == probeHandle) fileType = e.ObjectTypeIndex;
            }

            var entries = new List<Entry>();
            for (long i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<HandleEntry>(first + (nint)(i * entrySize));
                var pid = (int)e.UniqueProcessId;
                if (pid == self || pid is 0 or 4) continue;
                if (fileType is { } type && e.ObjectTypeIndex != type) continue;
                entries.Add(new Entry(pid, e.HandleValue));
            }

            return entries;
        }
        finally { Marshal.FreeHGlobal(table.Pointer); }
    }

    private static string? KernelNameOf(nint handle, nint buffer)
    {
        if (NtQueryObject(handle, ObjectNameInformation, buffer, NameBufferSize, out _) < 0) return null;
        var name = Marshal.PtrToStructure<UnicodeString>(buffer);
        if (name.Length == 0 || name.Buffer == 0) return null;
        return Marshal.PtrToStringUni(name.Buffer, name.Length / 2);
    }

    private static (nint Pointer, int Size) QuerySystemInformation(int informationClass)
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            var status = NtQuerySystemInformation(informationClass, buffer, size, out var needed);
            if (status >= 0) return (buffer, size);
            Marshal.FreeHGlobal(buffer);
            if (status != StatusInfoLengthMismatch || size >= 1 << 30) throw new Win32Exception($"NtQuerySystemInformation failed: 0x{status:X8}");
            size = Math.Max(size * 2, needed + (1 << 16));
        }
    }

    // ── Processes ────────────────────────────────────────────────────────────────────────────

    private static List<string> ModulesOf(int pid)
    {
        var result = new List<string>();
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (process == 0) return result;
        try
        {
            var modules = new nint[512];
            while (true)
            {
                if (!EnumProcessModulesEx(process, modules, modules.Length * IntPtr.Size, out var needed, ListModulesAll)) return result;
                if (needed <= modules.Length * IntPtr.Size)
                {
                    var buffer = new char[32768];
                    for (var i = 0; i < needed / IntPtr.Size; i++)
                    {
                        var length = GetModuleFileNameEx(process, modules[i], buffer, buffer.Length);
                        if (length > 0) result.Add(new string(buffer, 0, length));
                    }

                    return result;
                }

                modules = new nint[needed / IntPtr.Size];
            }
        }
        finally { CloseHandle(process); }
    }

    private static (string Name, string? Path) Describe(int pid)
    {
        string? path = null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process != 0)
        {
            try
            {
                var buffer = new char[32768];
                var length = buffer.Length;
                if (QueryFullProcessImageName(process, 0, buffer, ref length)) path = new string(buffer, 0, length);
            }
            finally { CloseHandle(process); }
        }

        if (path is not null) return (System.IO.Path.GetFileName(path), path);
        try
        {
            using var p = Process.GetProcessById(pid);
            return (p.ProcessName + ".exe", null);
        }
        catch { return ($"PID {pid}", null); }
    }

    /// <summary>DOMAIN\user that runs the process, or null when it cannot be read.</summary>
    public static string? UserOf(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.Name;
            }
            catch { return null; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    // ── Native ───────────────────────────────────────────────────────────────────────────────

    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int NameBufferSize = 64 * 1024;
    private const uint FileShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileTypeDisk = 1;
    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint DuplicateSameAccess = 2;
    private const uint ListModulesAll = 3;
    private const uint TokenQuery = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public nint Object;
        public nuint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, nint information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(nint handle, int informationClass, nint information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModulesEx(nint process, [Out] nint[] modules, int size, out int needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameExW")]
    private static extern int GetModuleFileNameEx(nint process, nint module, [Out] char[] name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
}

/// <summary>What <see cref="LockFinder.Find"/> found. <see cref="Partial"/>: some processes could not be read
/// (AresToys is not elevated, or a handle hung), so the list may be incomplete.</summary>
public sealed record LockSearchResult(IReadOnlyList<LockingProcess> Processes, bool Partial);
