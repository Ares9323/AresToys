using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AresToys.App.Services;

/// <summary>
/// Token-elevation detection + persistent "always run as administrator" toggle, mirroring the
/// PowerToys "Running as administrator" Settings flow:
///
///   • <see cref="IsProcessElevated"/> queries <c>GetTokenInformation(TokenElevation)</c>, the
///     authoritative API for detecting whether the current process is running with an elevated
///     token. We deliberately do NOT use <c>WindowsPrincipal.IsInRole(Administrator)</c>, which is
///     unreliable across UAC configurations (it sometimes returns true for the filtered token,
///     sometimes false depending on Windows build + the way <c>WindowsIdentity</c> was
///     constructed).
///
///   • <see cref="RunElevated"/> is the user-facing persistent preference ("Always run as
///     administrator") stored under <c>HKCU\Software\AresToys\RunElevated</c>. Registry rather
///     than the SQLite settings store because <c>App.OnStartup</c> must read it BEFORE the
///     DI host spins up — by that point we may have already lost the race to self-relaunch.
///
///   • <see cref="RestartElevated"/> re-launches the current EXE via <c>ShellExecuteEx</c> with
///     <c>Verb="runas"</c>, propagating a <c>--restarted-elevated</c> arg so the new instance
///     skips its own self-relaunch check and avoids a UAC prompt loop on denial. Caller is
///     expected to <c>Application.Current.Shutdown()</c> on a true return.
///
/// The "Always run as administrator" checkbox is intentionally only enabled in Settings when
/// AresToys is currently elevated — same UX as PowerToys. This sidesteps the entire class of
/// "Task Scheduler can't modify a HighestAvailable task from a non-elevated process" bugs
/// because every schtasks operation that needs elevation runs from an already-elevated context.
/// </summary>
public sealed class ElevationService
{
    private const string RegistryRoot = @"Software\AresToys";
    private const string RunElevatedValueName = "RunElevated";
    /// <summary>Command-line flag the elevated child uses to tell its own <c>App.OnStartup</c>
    /// "you've already been re-launched — don't bounce again". Without it, a UAC denial on the
    /// runas hop would leave us in an infinite restart loop the next time the user clicks the
    /// app icon.</summary>
    public const string RestartedElevatedArg = "--restarted-elevated";

    /// <summary>Symmetric counterpart for the unelevated restart path. The user clicked
    /// "Restart normally" from tray while running elevated; we hand-off to Explorer to launch a
    /// medium-IL child, and this flag tells the child's <c>App.OnStartup</c> to skip the
    /// "Always run as administrator" auto-elevation gate just for this session (the persisted
    /// preference is intentionally not cleared — Restart-normally is a one-shot bypass, not a
    /// settings change).</summary>
    public const string RestartedUnelevatedArg = "--restarted-unelevated";

    private readonly bool _isProcessElevated;

    public ElevationService()
    {
        _isProcessElevated = DetectProcessElevation();
    }

    /// <summary>True when the current process is running with an elevated token. Cached at
    /// construction — elevation doesn't change mid-process so we don't re-query.</summary>
    public bool IsProcessElevated => _isProcessElevated;

    /// <summary>Persisted "Always run as administrator" preference. Reads / writes
    /// <c>HKCU\Software\AresToys\RunElevated</c> (DWORD 0 or 1). Returns false if the key doesn't
    /// exist (fresh installs default to "no auto-elevation").</summary>
    public bool RunElevated
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryRoot, writable: false);
                if (key is null) return false;
                return (key.GetValue(RunElevatedValueName) is int v) && v != 0;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryRoot, writable: true);
                key?.SetValue(RunElevatedValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch
            {
                // Locked-down HKCU is rare on a user-mode install; if it happens the toggle just
                // doesn't persist and the user can re-click after restart. Don't crash the VM.
            }
        }
    }

    /// <summary>Static early-startup variant for <c>App.OnStartup</c> — runs before DI is built
    /// so we can decide whether to self-relaunch before the heavy WPF + host bootstrap.</summary>
    public static bool ReadRunElevatedFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRoot, writable: false);
            if (key is null) return false;
            return (key.GetValue(RunElevatedValueName) is int v) && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Re-launches the current EXE with <c>Verb="runas"</c> and the
    /// <see cref="RestartedElevatedArg"/> flag. Returns true on success; caller should then
    /// <c>Application.Current.Shutdown()</c> to drop the current non-elevated instance. Returns
    /// false on UAC denial / cancel / any ShellExecuteEx error — in that case the caller stays
    /// in the current (non-elevated) session.</summary>
    public static bool RestartElevated()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return false;

        try
        {
            var psi = new ProcessStartInfo(exePath, RestartedElevatedArg)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? string.Empty,
            };
            var p = Process.Start(psi);
            return p is not null;
        }
        catch
        {
            // User cancelled UAC (Win32Exception 1223 ERROR_CANCELLED) or some other shell-exec
            // failure. Swallow and return false — the toggle's setter will read the post-call
            // state and the UI stays in sync.
            return false;
        }
    }

    /// <summary>Re-launches the current EXE at medium integrity even when the caller is elevated.
    /// Borrows explorer.exe's primary token (medium-IL by design — the shell process always runs
    /// as the interactive user, never elevated) and spawns the child with that token via
    /// <c>CreateProcessWithTokenW</c>. The new process inherits Explorer's integrity level, UAC
    /// linked-token, and session — exactly what "Run as limited user" tools (Process Explorer,
    /// Sysinternals, PowerToys' RestartHelper) do under the hood. Returns true on success; caller
    /// should <c>Application.Current.Shutdown()</c> to drop the current elevated instance. Returns
    /// false if explorer.exe isn't found (kiosk / audit-mode session) or any of the token / process
    /// APIs fail — caller stays in the elevated session in that case.
    ///
    /// Note: the <c>Shell.Application.ShellExecute</c> COM trick is NOT used here even though it
    /// looks simpler — Shell.Application activated from an elevated process is a same-integrity
    /// in-proc instance, so its ShellExecute also runs elevated. The child would still be admin.
    /// Only token borrowing actually drops the IL.</summary>
    public static bool RestartUnelevated()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return false;

        // Pick any explorer.exe in this session — typically there's exactly one (the shell), so
        // FirstOrDefault is fine. We don't filter on SessionId because TS sessions get their own
        // shell process and we're inheriting the user's interactive token either way.
        var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
        if (explorer is null) return false;

        IntPtr explorerProcessToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(explorer.Handle,
                    TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_IMPERSONATE,
                    out explorerProcessToken))
                return false;

            // DuplicateTokenEx → primary token usable as the child's process token. Impersonation
            // level can stay at SecurityImpersonation; only the token type matters for spawning.
            if (!DuplicateTokenEx(explorerProcessToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
                return false;

            var workingDir = System.IO.Path.GetDirectoryName(exePath) ?? string.Empty;
            // CreateProcessWithTokenW takes a mutable command line buffer (the API may modify it
            // in-place to NUL-separate argv[0] from the rest). We pass it as a writable string.
            var cmdLine = $"\"{exePath}\" {RestartedUnelevatedArg}";
            var si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessWithTokenW(
                    primaryToken,
                    0,
                    null,
                    cmdLine,
                    0,
                    IntPtr.Zero,
                    workingDir,
                    ref si,
                    out var pi))
                return false;

            // We don't wait on / interact with the child — release the handles immediately.
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (explorerProcessToken != IntPtr.Zero) CloseHandle(explorerProcessToken);
        }
    }

    // ── TokenElevation P/Invoke ───────────────────────────────────────────────────────────────

    private static bool DetectProcessElevation()
    {
        SafeFileHandle? tokenHandle = null;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out tokenHandle) || tokenHandle.IsInvalid)
                return false;

            var elevation = new TOKEN_ELEVATION();
            var elevationSize = (uint)Marshal.SizeOf<TOKEN_ELEVATION>();
            if (!GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevation,
                    ref elevation, elevationSize, out _))
                return false;

            return elevation.TokenIsElevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            tokenHandle?.Dispose();
        }
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_IMPERSONATE = 0x0004;
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation,
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out SafeFileHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenProcessToken")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        string lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeFileHandle TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        ref TOKEN_ELEVATION TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);
}
