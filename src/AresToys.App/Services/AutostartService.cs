using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace AresToys.App.Services;

/// <summary>
/// Windows autostart toggle. Registers a Task Scheduler "at logon" task under the current user
/// — this bypasses the ~10-second <c>StartupDelayInMSec</c> Windows imposes on
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> entries (the user's reported
/// symptom: "AresToys starts last, after most other startup apps"). The task also runs at
/// "Above Normal" priority (Task XML <c>&lt;Priority&gt;4&lt;/Priority&gt;</c>) so the dispatcher
/// + tray + clipboard hook come up before lower-priority Run-key apps finish their hydration.
///
/// Tradeoff: the task is managed in Task Scheduler (Microsoft → Windows → AresToys-style entry,
/// but at the library root — see <see cref="TaskName"/>), not under Task Manager → Startup apps.
/// Users who want to manage it from there will be surprised; the toggle in our Settings panel
/// remains the single source of truth.
///
/// Migration: if a stale <c>HKCU\…\Run</c> entry from a previous version is present, every call
/// to <see cref="SetEnabled"/> cleans it up so the user doesn't end up with two competing
/// autostart paths firing in parallel.
/// </summary>
public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "AresToys";
    /// <summary>Task name as it appears in <c>schtasks /Query</c> and Task Scheduler. Kept flat
    /// at the library root rather than nested under a folder so the user can find / delete it
    /// without drilling.</summary>
    private const string TaskName = "AresToysAutostart";

    public bool IsEnabled => TaskExists();

    /// <summary>True when the registered task uses <c>&lt;RunLevel&gt;HighestAvailable&lt;/RunLevel&gt;</c>,
    /// i.e. it starts AresToys elevated at logon (no UAC prompt at each logon — Task Scheduler treats
    /// the task itself as pre-approved). Used by the Settings toggle to reflect the current state
    /// truthfully even after a UAC denial: if the user cancels the elevation prompt during
    /// <see cref="SetEnabled(bool, bool)"/>, the previous run-level survives and this property reads it back.</summary>
    public bool IsElevated => TaskRunLevelIsHighest();

    public void SetEnabled(bool enabled, bool elevated = false)
    {
        if (enabled)
        {
            CreateOrReplaceTask(elevated);
        }
        else
        {
            DeleteTaskIfExists();
        }
        // Always clean up the legacy Run-key entry from older AresToys versions so we don't
        // end up with both paths racing each other at logon.
        TryRemoveLegacyRunKeyEntry();
    }

    // ── Task Scheduler interaction via schtasks.exe ───────────────────────────────────────────
    //
    // We shell out to schtasks instead of P/Invoking the COM TaskScheduler / using a NuGet
    // wrapper because:
    //   - schtasks ships with every Windows install since XP, no version skew
    //   - the COM interfaces have a notoriously brittle marshaling surface (TASK_LOGON_TYPE,
    //     ITaskFolder, IRegisteredTask) and getting the typelib bindings right would add code
    //     for zero behavioural win
    //   - schtasks /XML takes the full Task XML schema so we can express every setting we care
    //     about (priority, multi-instance policy, idle behaviour) declaratively in one string

    private static bool TaskExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            // schtasks returns 0 when the task exists, 1 (with "ERROR: The system cannot find
            // the file specified.") when it doesn't. Anything else (timeout, schtasks missing
            // somehow) we treat as "not registered" so the toggle reads as off and the user can
            // click to enable cleanly.
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CreateOrReplaceTask(bool elevated)
    {
        var xml = BuildTaskXml(elevated);
        var tmpDir = Path.GetTempPath();
        var tmpPath = Path.Combine(tmpDir, $"arestoys-autostart-{Guid.NewGuid():N}.xml");
        // Task XML must be UTF-16 (per the official schema declaration) — UTF-8 makes schtasks
        // refuse the file with an XML parse error.
        File.WriteAllText(tmpPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            // Registering a task with <RunLevel>HighestAvailable</RunLevel> requires the schtasks
            // process itself to be elevated — schtasks won't escalate the run-level beyond the
            // caller's. We hop UAC via Verb="runas" (one prompt, only at the moment the user flips
            // the toggle; subsequent logons start the task elevated without any prompt because
            // Task Scheduler treats the registered task as pre-approved).
            //
            // When elevated=false we keep the plain non-elevated invocation so toggling autostart
            // off/on doesn't trigger spurious UAC prompts.
            var psi = elevated
                ? new ProcessStartInfo("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{tmpPath}\" /F")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
                : new ProcessStartInfo("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{tmpPath}\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            using var p = Process.Start(psi);
            p?.WaitForExit(elevated ? 30000 : 5000);
        }
        catch
        {
            // Surface nothing — the Settings toggle will read back IsEnabled / IsElevated after
            // the call and flip itself to the actual on-disk state if the task didn't get created
            // (e.g. user denied UAC). Silent failure is preferable to popping a MessageBox during
            // DI hydration of the Settings VM.
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static void DeleteTaskIfExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Delete /TN \"{TaskName}\" /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(3000);
        }
        catch
        {
            // Same rationale as CreateOrReplaceTask — Settings re-reads IsEnabled.
        }
    }

    private static bool TaskRunLevelIsHighest()
    {
        if (!TaskExists()) return false;
        // schtasks /Query /XML emits the task XML to stdout using the active console code page
        // (UTF-8 on modern Windows, OEM codepage on older configs) — NOT UTF-16, despite the XML
        // declaration <?xml version="1.0" encoding="UTF-16"?> baked into the document. Setting
        // StandardOutputEncoding=Unicode here was a bug: every byte pair got decoded as one
        // garbled UTF-16 code unit and the Contains() check never matched.
        //
        // To dodge encoding ambiguity entirely we redirect schtasks output to a temp file via
        // cmd.exe ">", then load it with the default-encoding-detecting StreamReader. The
        // substring "HighestAvailable" appears only in the <RunLevel> element across all
        // schtasks-emitted XML, so a plain Contains() suffices — no XML parsing needed.
        var tmpPath = Path.Combine(Path.GetTempPath(), $"arestoys-query-{Guid.NewGuid():N}.xml");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c schtasks /Query /TN \"{TaskName}\" /XML > \"{tmpPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            if (!File.Exists(tmpPath)) return false;
            var xml = File.ReadAllText(tmpPath);
            return xml.Contains("HighestAvailable", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildTaskXml(bool elevated)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("Could not resolve current executable path");
        var workDir = Path.GetDirectoryName(exe) ?? string.Empty;
        // WindowsIdentity.Name returns "DOMAIN\username" — exactly what the Task XML expects for
        // <UserId> in both <LogonTrigger> and <Principal>. Falling back to Environment if Identity
        // is somehow unavailable (sandboxed test runs).
        string userId;
        try { userId = WindowsIdentity.GetCurrent().Name; }
        catch { userId = $@"{Environment.UserDomainName}\{Environment.UserName}"; }

        // Notes on each setting:
        //   - <Priority>4</Priority> = ABOVE_NORMAL_PRIORITY_CLASS (lower number = higher priority
        //     per the schema; 4-6 are valid for non-elevated tasks). Gives the dispatcher /
        //     tray init a leg up over default-priority Run-key startup apps.
        //   - MultipleInstancesPolicy=IgnoreNew because we already enforce single-instance via
        //     the pipe IPC; a redundant launch from the task would be no-op'd anyway, but
        //     telling Task Scheduler not to spawn one keeps logs clean.
        //   - DisallowStartIfOnBatteries=false / StopIfGoingOnBatteries=false: laptop users
        //     unplugged at logon would otherwise never see AresToys start.
        //   - ExecutionTimeLimit=PT0S = "no time limit" (the default 72 h would otherwise let
        //     Windows kill the process after 3 days of uninterrupted uptime).
        //   - LogonType=InteractiveToken + RunLevel=LeastPrivilege (or HighestAvailable when the
        //     user opted in via the Settings toggle). HighestAvailable means the task action runs
        //     with the full admin token of the user *if* they're a member of Administrators —
        //     otherwise it silently degrades to the standard token. Either way, no UAC prompt at
        //     logon: Task Scheduler treats the registered task as pre-approved.
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine("  <RegistrationInfo>");
        sb.AppendLine("    <Description>AresToys autostart (Task Scheduler — avoids the HKCU\\Run startup delay)</Description>");
        sb.AppendLine("  </RegistrationInfo>");
        sb.AppendLine("  <Triggers>");
        sb.AppendLine("    <LogonTrigger>");
        sb.AppendLine("      <Enabled>true</Enabled>");
        sb.Append("      <UserId>").Append(EscapeXml(userId)).AppendLine("</UserId>");
        sb.AppendLine("    </LogonTrigger>");
        sb.AppendLine("  </Triggers>");
        sb.AppendLine("  <Principals>");
        sb.AppendLine("    <Principal id=\"Author\">");
        sb.Append("      <UserId>").Append(EscapeXml(userId)).AppendLine("</UserId>");
        sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        sb.Append("      <RunLevel>").Append(elevated ? "HighestAvailable" : "LeastPrivilege").AppendLine("</RunLevel>");
        sb.AppendLine("    </Principal>");
        sb.AppendLine("  </Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        sb.AppendLine("    <IdleSettings>");
        sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        sb.AppendLine("    </IdleSettings>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        sb.AppendLine("    <Enabled>true</Enabled>");
        sb.AppendLine("    <Hidden>false</Hidden>");
        sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        sb.AppendLine("    <WakeToRun>false</WakeToRun>");
        sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        sb.AppendLine("    <Priority>4</Priority>");
        sb.AppendLine("    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>");
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\">");
        sb.AppendLine("    <Exec>");
        sb.Append("      <Command>").Append(EscapeXml(exe)).AppendLine("</Command>");
        if (!string.IsNullOrEmpty(workDir))
            sb.Append("      <WorkingDirectory>").Append(EscapeXml(workDir)).AppendLine("</WorkingDirectory>");
        sb.AppendLine("    </Exec>");
        sb.AppendLine("  </Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static void TryRemoveLegacyRunKeyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (key.GetValue(LegacyValueName) is null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort: a locked-down Run key (group policy, AV) shouldn't crash the toggle.
        }
    }
}
