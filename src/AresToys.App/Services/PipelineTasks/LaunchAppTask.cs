using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Launches an executable / shortcut / batch file. Config keys: <c>path</c> (required, the
/// target — accepts %ENV% expansion), <c>args</c> (optional command-line), <c>workingDir</c>
/// (optional, defaults to the path's directory). Uses <c>UseShellExecute=true</c> so .exe,
/// .lnk, .bat, .cmd, and even URL protocols all resolve through the shell. The MaxLaunchpad
/// equivalent — but composable into any AresToys workflow.
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
        if (string.IsNullOrEmpty(workingDir))
        {
            // Default the working directory to the target's parent so the launched app finds its
            // own resources (matches how Explorer would launch it on double-click).
            try { workingDir = Path.GetDirectoryName(path) ?? string.Empty; }
            catch { workingDir = string.Empty; }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = workingDir,
            };
            Process.Start(psi);
            _logger.LogInformation("LaunchAppTask: launched {Path} {Args}", path, args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LaunchAppTask: failed to launch {Path}", path);
        }
        return Task.CompletedTask;
    }
}
