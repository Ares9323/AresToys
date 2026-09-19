using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Launcher;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Launches an executable / shortcut / batch file. Config keys: <c>path</c> (required, the
/// target — accepts %ENV% expansion), <c>args</c> (optional command-line), <c>workingDir</c>
/// (optional, defaults to the path's directory), <c>windowMode</c> (Normal / Maximized /
/// Minimized / Hidden) and <c>runAsAdmin</c> (elevate through the UAC prompt). Uses
/// <c>UseShellExecute=true</c> so .exe, .lnk, .bat, .cmd, URL protocols and packaged-app
/// targets all resolve through the shell. The MaxLaunchpad equivalent — but composable into
/// any AresToys workflow, and sharing its window-mode / elevation semantics with the launcher.
/// </summary>
public sealed class LaunchAppTask : IPipelineTask
{
    public const string TaskId = "arestoys.launch-app";

    private readonly ILogger<LaunchAppTask> _logger;

    public LaunchAppTask(ILogger<LaunchAppTask> logger) { _logger = logger; }

    public string Id => TaskId;
    public string DisplayName => "Launch app";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Resolution order: hardcoded config.path > bag.text. Lets a workflow chain "Read
        // clipboard → Launch app" when the clipboard holds an executable / shortcut path.
        // args / workingDir stay config-only — using bag.text for those would mix execution
        // semantics in confusing ways. Hardcoded path always wins.
        var rawPath = (string?)config?["path"];
        if (string.IsNullOrWhiteSpace(rawPath)
            && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawBag) && rawBag is string fromBag)
        {
            rawPath = fromBag;
        }
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            _logger.LogWarning("LaunchAppTask: no path configured + no bag.text; skipping");
            return Task.CompletedTask;
        }
        var path = Environment.ExpandEnvironmentVariables(rawPath).Trim();
        var args = Environment.ExpandEnvironmentVariables((string?)config?["args"] ?? string.Empty);
        var workingDir = Environment.ExpandEnvironmentVariables((string?)config?["workingDir"] ?? string.Empty);
        var windowMode = (string?)config?["windowMode"];
        var runAsAdmin = (bool?)config?["runAsAdmin"] ?? false;

        var psi = BuildStartInfo(path, args, workingDir, windowMode, runAsAdmin);
        try
        {
            Process.Start(psi);
            _logger.LogInformation("LaunchAppTask: launched {Path} {Args} (admin={Admin}, mode={Mode})",
                psi.FileName, args, runAsAdmin, psi.WindowStyle);
        }
        catch (Exception ex)
        {
            // Includes the user cancelling the UAC prompt on an elevated launch (Win32Exception
            // 1223) — a warning, not a workflow-breaking error.
            _logger.LogWarning(ex, "LaunchAppTask: failed to launch {Path}", psi.FileName);
        }
        return Task.CompletedTask;
    }

    /// <summary>Turn the step's settings into the process to start. Split out from
    /// <see cref="ExecuteAsync"/> so the mapping is testable without launching anything.
    /// <paramref name="path"/> is expected to be already env-expanded.</summary>
    public static ProcessStartInfo BuildStartInfo(
        string path, string args, string workingDir, string? windowMode, bool runAsAdmin)
    {
        // A packaged (MSIX/UWP) app is identified by an AppUserModelID, which ShellExecute only
        // accepts in its shell:AppsFolder form — same normalisation the launcher applies.
        var target = PackagedAppPath.Normalize(path);

        if (string.IsNullOrEmpty(workingDir) && !PackagedAppPath.IsAppsFolderPath(target))
        {
            // Default the working directory to the target's parent so the launched app finds its
            // own resources (matches how Explorer would launch it on double-click). A shell
            // parsing name has no parent directory, hence the guard.
            try { workingDir = Path.GetDirectoryName(target) ?? string.Empty; }
            catch { workingDir = string.Empty; }
        }

        var psi = new ProcessStartInfo
        {
            FileName = target,
            Arguments = args,
            UseShellExecute = true,   // .lnk / .bat / URL / AppsFolder resolution, and required for runas
            WorkingDirectory = workingDir,
            WindowStyle = ParseWindowStyle(windowMode),
        };
        // "runas" is what raises the UAC prompt; without it the child inherits our integrity level.
        if (runAsAdmin) psi.Verb = "runas";
        return psi;
    }

    /// <summary>Parse the step's <c>windowMode</c> string into a window style. Unknown or missing
    /// values fall back to Normal so an old profile (saved before this option existed) or a
    /// hand-edited config can't break the step.</summary>
    private static ProcessWindowStyle ParseWindowStyle(string? windowMode) =>
        Enum.TryParse<LauncherWindowMode>(windowMode, ignoreCase: true, out var mode)
            ? mode switch
            {
                LauncherWindowMode.Maximized => ProcessWindowStyle.Maximized,
                LauncherWindowMode.Minimized => ProcessWindowStyle.Minimized,
                LauncherWindowMode.Hidden => ProcessWindowStyle.Hidden,
                _ => ProcessWindowStyle.Normal,
            }
            : ProcessWindowStyle.Normal;
}
