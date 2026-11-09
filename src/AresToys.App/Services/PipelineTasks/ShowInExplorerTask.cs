using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Opens an Explorer window with the just-saved file pre-selected (<c>explorer /select,"path"</c>).
/// Reads <c>bag.local_path</c> from a preceding save-to-file step. Falls back to opening the
/// containing folder if the path no longer exists, and silently skips when no path is in bag.
/// </summary>
public sealed class ShowInExplorerTask : IPipelineTask
{
    public const string TaskId = "arestoys.show-in-explorer";

    private readonly ILogger<ShowInExplorerTask> _logger;

    public ShowInExplorerTask(ILogger<ShowInExplorerTask> logger) { _logger = logger; }

    public string Id => TaskId;
    public string DisplayName => "Show file in Explorer";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        if (!context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var raw) || raw is not string path || string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("ShowInExplorerTask: no local_path in bag (run a save-to-file step first); skipping");
            return Task.CompletedTask;
        }
        try
        {
            if (File.Exists(path))
            {
                // /select,"X" tells Explorer to open the parent folder and pre-select the file.
                // Quotes around the path are required when it contains spaces.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                // File got moved / deleted between save and this step — fall back to opening the folder.
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{folder}\"", UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShowInExplorerTask: failed to launch explorer for {Path}", path);
        }
        return Task.CompletedTask;
    }
}
