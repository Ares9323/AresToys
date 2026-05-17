using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Opens a file or folder with its default OS-registered application — same effect as a
/// double-click in Explorer. Config: <c>path</c> (optional, %ENV% expanded). When the
/// config path is empty the task falls back to <c>bag.text</c> from an upstream step (e.g.
/// "Read clipboard" producing a path string). Config path always wins so a hardcoded target
/// can't be hijacked by bag content. For files this goes through the shell, so PDFs land in
/// the PDF reader, .txt in Notepad, etc. For folders it opens an Explorer window. Distinct
/// from <see cref="LaunchAppTask"/> which always treats the target as an executable to spawn
/// directly.
/// </summary>
public sealed class OpenFileTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-file";

    private readonly ILogger<OpenFileTask> _logger;

    public OpenFileTask(ILogger<OpenFileTask> logger) { _logger = logger; }

    public string Id => TaskId;
    public string DisplayName => "Open file / folder";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Resolution order: config.path > bag.text > skip. Config wins so a workflow with a
        // hardcoded folder isn't redirected by stray clipboard content; bag.text picks up the
        // common "Read clipboard → Open file" chain when the user wants the target to come
        // from whatever they just copied.
        var rawPath = (string?)config?["path"];
        if (string.IsNullOrWhiteSpace(rawPath)
            && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawBag) && rawBag is string fromBag)
        {
            rawPath = fromBag;
        }
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            _logger.LogWarning("OpenFileTask: no path configured + no bag.text; skipping");
            return Task.CompletedTask;
        }
        var path = Environment.ExpandEnvironmentVariables(rawPath).Trim();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            _logger.LogInformation("OpenFileTask: opened {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenFileTask: failed to open {Path}", path);
        }
        return Task.CompletedTask;
    }
}
