using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Launcher;

/// <summary>Minimises an app we just started, once it has a window to minimise.
///
/// The <c>Minimized</c> window mode is only a request: it travels to the child in STARTUPINFO's
/// <c>wShowWindow</c> and the app decides what to do with it. Tray-dwelling apps routinely
/// answer by showing no window at all, which is indistinguishable from a launch that failed,
/// and some single-instance apps hand off to the running copy and exit before anything is
/// drawn. <see cref="LauncherWindowMode.MinimizeAfterStartup"/> avoids the whole conversation:
/// start the app the way it likes to be started (normal), wait for its window, then minimise it
/// ourselves.
///
/// Everything here is best-effort and off the calling thread: a workflow step or a launcher key
/// must not block while an app boots.</summary>
public static class WindowMinimizer
{
    /// <summary>How long to keep looking for a window before giving up. Generous because this
    /// covers cold starts of heavy apps; nothing is held open meanwhile except a timer.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Watch for the launched app's window and minimise it. <paramref name="started"/>
    /// is what <see cref="Process.Start(ProcessStartInfo)"/> handed back, which is null for shell
    /// targets (a packaged app, a .url, a document opened by association) and for a process that
    /// the shell handed to an already-running instance. <paramref name="targetPath"/> is the
    /// launch target, used to work out a process name to look for when the started process isn't
    /// the one that owns the window: exactly what a single-instance app does when the second copy
    /// hands off and exits.</summary>
    public static void MinimizeWhenReady(Process? started, string? targetPath, ILogger? logger = null)
    {
        var processName = ProcessNameFrom(targetPath);
        _ = Task.Run(() => WatchAndMinimize(started, processName, logger));
    }

    private static void WatchAndMinimize(Process? started, string? processName, ILogger? logger)
    {
        var giveUpAt = DateTime.UtcNow + Deadline;
        try
        {
            while (DateTime.UtcNow < giveUpAt)
            {
                // The process we started, while it's alive and has drawn something.
                if (started is not null)
                {
                    try
                    {
                        if (!started.HasExited)
                        {
                            started.Refresh();
                            if (Minimize(started.MainWindowHandle))
                            {
                                logger?.LogDebug("WindowMinimizer: minimised the window of pid {Pid}", started.Id);
                                return;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process object outlived the process itself; fall through to the name scan.
                    }
                }

                // Nothing of our own to minimise: look for a window belonging to another instance
                // of the same executable. This is the single-instance case, where the copy we
                // started forwarded the request and exited, leaving the window to the copy that
                // was already running.
                if (processName is not null && MinimizeByProcessName(processName))
                {
                    logger?.LogDebug("WindowMinimizer: minimised an existing window of '{Process}'", processName);
                    return;
                }

                Thread.Sleep(PollInterval);
            }
            logger?.LogDebug("WindowMinimizer: no window showed up for '{Process}' within {Seconds}s",
                processName ?? "(unknown)", Deadline.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Nothing here is worth surfacing: the app did start, it just isn't minimised.
            logger?.LogDebug(ex, "WindowMinimizer: gave up on '{Process}'", processName ?? "(unknown)");
        }
    }

    private static bool MinimizeByProcessName(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                using (proc)
                {
                    if (Minimize(proc.MainWindowHandle)) return true;
                }
            }
        }
        catch
        {
            // Process API throws on permission edge cases; treat as "nothing to minimise yet".
        }
        return false;
    }

    /// <summary>Minimise a window, unless it's already minimised (in which case there's nothing
    /// to do and we report success so the caller stops watching). False for a null handle, so
    /// the poll loop can use it as its "not ready yet" answer.</summary>
    private static bool Minimize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) return true;
        // SW_MINIMIZE rather than SW_SHOWMINNOACTIVE: the window is on screen and focused by the
        // time we get here, so the next window in the Z order should come forward as it goes down.
        return ShowWindow(hwnd, SW_MINIMIZE);
    }

    /// <summary>The process name Process.GetProcessesByName expects (no directory, no ".exe"),
    /// or null when the target isn't a plain executable path: a shell parsing name, a document,
    /// a URL. Those have no predictable process behind them, so the name scan is skipped and we
    /// rely on the started process alone.</summary>
    private static string? ProcessNameFrom(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        var path = targetPath.Trim();
        if (PackagedAppPath.IsAppsFolderPath(path)) return null;
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────────

    private const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
}
