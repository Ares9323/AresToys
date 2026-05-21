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

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out SafeFileHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeFileHandle TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        ref TOKEN_ELEVATION TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);
}
