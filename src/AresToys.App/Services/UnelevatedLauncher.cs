using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AresToys.App.Services;

/// <summary>Starts what the user launches from Wormholes, the Launcher and workflows. AresToys often runs elevated
/// (a Task Scheduler entry with the highest privileges), and a plain <c>Process.Start</c> would hand every child
/// that elevated token. Here the child gets the shell's token instead, unless the entry asked for
/// <c>Verb = "runas"</c>, which stays the explicit way to run as administrator.</summary>
public static class UnelevatedLauncher
{
    private const int ErrorElevationRequired = 740;
    private const int ErrorPrivilegeNotHeld = 1314;

    /// <summary>Same contract as <see cref="Process.Start(ProcessStartInfo)"/>: null when the shell started the
    /// process and no handle is known.</summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        if (!ElevationService.IsCurrentProcessElevated || string.Equals(psi.Verb, "runas", StringComparison.OrdinalIgnoreCase))
            return Process.Start(psi);

        try
        {
            if (TryStartWithShellToken(psi, out var started)) return started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorElevationRequired or ErrorPrivilegeNotHeld)
        {
            // ErrorElevationRequired: the program itself demands administrator rights, so the shell shows the UAC prompt.
            // ErrorPrivilegeNotHeld: this token cannot hand out the shell's; launch as before rather than not at all.
        }

        return Process.Start(psi);
    }

    private static bool TryStartWithShellToken(ProcessStartInfo psi, out Process? started)
    {
        started = null;
        var target = psi.FileName;
        var args = psi.Arguments ?? string.Empty;
        var show = ShowWindowFor(psi.WindowStyle);
        var directory = psi.WorkingDirectory;

        var file = ResolveProgram(target);
        int pid;
        bool ok;
        if (file is not null && IsScript(file))
        {
            // cmd's own quoting: the whole command after /c goes inside one more pair of quotes.
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            ok = ElevationService.TryStartWithShellToken(cmd, $"\"{cmd}\" /c \"\"{file}\" {args}\"", WorkingDirectoryFor(directory, file), show, out pid);
        }
        else if (file is not null)
        {
            ok = ElevationService.TryStartWithShellToken(null, $"\"{file}\" {args}".TrimEnd(), WorkingDirectoryFor(directory, file), show, out pid);
        }
        else
        {
            // A shortcut, a document, a URL, a folder or a packaged app: explorer.exe asks the running shell to open
            // it, and that shell is not elevated. A shortcut keeps its own arguments; ours cannot ride along.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            ok = ElevationService.TryStartWithShellToken(explorer, $"\"{explorer}\" \"{target}\"", null, -1, out pid);
        }

        if (!ok) return false;
        if (file is not null)
        {
            try { started = Process.GetProcessById(pid); } catch (ArgumentException) { /* already gone */ }
        }
        return true;
    }

    /// <summary>The program file the target names when it is an executable or script that exists (a full path, or a
    /// bare name found on PATH); null for everything the shell has to resolve (shortcuts, documents, URLs).</summary>
    private static string? ResolveProgram(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var extension = Path.GetExtension(target);
        if (!IsProgram(extension) && !IsScriptExtension(extension)) return null;

        try
        {
            if (Path.IsPathRooted(target)) return File.Exists(target) ? target : null;

            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(folder.Trim('"'), target);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (ArgumentException)
        {
            // An invalid path character: let the shell report it.
        }
        return null;
    }

    private static bool IsProgram(string extension) =>
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptExtension(string extension) =>
        extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);

    private static bool IsScript(string file) => IsScriptExtension(Path.GetExtension(file));

    private static string? WorkingDirectoryFor(string? requested, string file) =>
        string.IsNullOrWhiteSpace(requested) ? Path.GetDirectoryName(file) : requested;

    private static int ShowWindowFor(ProcessWindowStyle style) => style switch
    {
        ProcessWindowStyle.Hidden => 0,
        ProcessWindowStyle.Minimized => 2,
        ProcessWindowStyle.Maximized => 3,
        _ => 1,
    };
}
