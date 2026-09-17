using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AresToys.Updater;

/// <summary>A process holding a file in the install folder open, surfaced to the user
/// before an update so it can be closed. See <see cref="InstallLockGuard"/>.</summary>
public sealed record LockingProcess(string AppName, string ProcessName, int Pid);

/// <summary>Raw locker as returned by the Restart Manager, before filtering. Carries the
/// "critical system process" flag RM sets so we never close one.</summary>
public sealed record LockerCandidate(int Pid, string ProcessName, string AppName, bool IsCriticalSystemProcess);

public static partial class InstallLockGuard
{
    /// <summary>Drop the processes we must never close: the current process, any other AresToys
    /// instance (Velopack closes those itself), and Windows-critical system processes. Pure and
    /// testable — the native RM query feeds it <see cref="LockerCandidate"/>s.</summary>
    public static IReadOnlyList<LockingProcess> FilterLockers(IEnumerable<LockerCandidate> candidates, int currentPid)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(c => c.Pid != currentPid)
            .Where(c => !c.IsCriticalSystemProcess)
            .Where(c => !IsAresToys(c.ProcessName))
            .Select(c => new LockingProcess(c.AppName, c.ProcessName, c.Pid))
            .ToList();
    }

    private static bool IsAresToys(string processName) =>
        string.Equals(processName, "AresToys", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "AresToys.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Query the Windows Restart Manager for the processes holding any file under
    /// <paramref name="installDir"/> open, then filter out self / other AresToys / critical
    /// processes. Returns an empty list on any RM failure (caller proceeds as normal).</summary>
    public static IReadOnlyList<LockingProcess> FindLockers(string installDir, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return Array.Empty<LockingProcess>();

        string[] files;
        try
        {
            files = Directory.GetFiles(installDir, "*.exe")
                .Concat(Directory.GetFiles(installDir, "*.dll"))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "InstallLockGuard: could not enumerate install files");
            return Array.Empty<LockingProcess>();
        }
        if (files.Length == 0) return Array.Empty<LockingProcess>();

        var key = new char[CCH_RM_SESSION_KEY + 1];
        var rv = RmStartSession(out var session, 0, key);
        if (rv != 0)
        {
            logger?.LogWarning("InstallLockGuard: RmStartSession failed ({Code})", rv);
            return Array.Empty<LockingProcess>();
        }
        try
        {
            rv = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (rv != 0)
            {
                logger?.LogWarning("InstallLockGuard: RmRegisterResources failed ({Code})", rv);
                return Array.Empty<LockingProcess>();
            }

            uint pnProcInfoNeeded = 0, pnProcInfo = 0, rebootReasons = 0;
            rv = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, null, out rebootReasons);
            if (rv != ERROR_MORE_DATA)
            {
                // 0 with 0 processes = nothing locking; anything else = give up quietly.
                if (rv == 0) return Array.Empty<LockingProcess>();
                logger?.LogWarning("InstallLockGuard: RmGetList(size) failed ({Code})", rv);
                return Array.Empty<LockingProcess>();
            }

            var info = new RM_PROCESS_INFO[pnProcInfoNeeded];
            pnProcInfo = pnProcInfoNeeded;
            rv = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, info, out rebootReasons);
            if (rv != 0)
            {
                logger?.LogWarning("InstallLockGuard: RmGetList(data) failed ({Code})", rv);
                return Array.Empty<LockingProcess>();
            }

            var candidates = new List<LockerCandidate>((int)pnProcInfo);
            for (var i = 0; i < pnProcInfo; i++)
            {
                var pid = info[i].Process.dwProcessId;
                var isCritical = info[i].ApplicationType == RM_APP_TYPE.RmCritical;
                var procName = TryGetProcessName(pid);
                var appName = string.IsNullOrWhiteSpace(info[i].strAppName) ? procName : info[i].strAppName;
                candidates.Add(new LockerCandidate(pid, procName, appName, isCritical));
            }
            return FilterLockers(candidates, Environment.ProcessId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "InstallLockGuard: RM query threw");
            return Array.Empty<LockingProcess>();
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static string TryGetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "unknown"; }
    }

    /// <summary>Close the given processes: ask the main window to close first (graceful), then
    /// force-kill any that don't exit within <paramref name="graceTimeout"/>. Tray helpers without a
    /// main window (e.g. acrotray) fall straight through to kill. Swallows per-process errors
    /// (already-exited / access-denied) so one stubborn process doesn't abort the update.</summary>
    public static void CloseLockers(IEnumerable<LockingProcess> processes, TimeSpan graceTimeout, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        foreach (var p in processes)
        {
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                var askedNicely = false;
                try { askedNicely = proc.CloseMainWindow(); } catch { /* no main window */ }
                if (askedNicely && proc.WaitForExit((int)graceTimeout.TotalMilliseconds))
                    continue;
                if (!proc.HasExited)
                {
                    proc.Kill();
                    // Kill() only requests termination; wait so the OS has actually torn the
                    // process down and released its file handles before we let the update apply.
                    proc.WaitForExit((int)graceTimeout.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "InstallLockGuard: could not close {Proc} (pid {Pid})", p.ProcessName, p.Pid);
            }
        }
    }

    // --- Restart Manager interop ---
    private const int CCH_RM_SESSION_KEY = 32;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);
}
