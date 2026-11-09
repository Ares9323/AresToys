using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Pauses the workflow for the configured number of milliseconds. Useful between paste / press-key
/// steps when the target window needs more time to process the previous keystroke than the default
/// margin allows (e.g. slow web editors, RDP sessions).
/// </summary>
public sealed class DelayTask : IPipelineTask
{
    public const string TaskId = "arestoys.delay";

    private readonly ILogger<DelayTask> _logger;

    public DelayTask(ILogger<DelayTask> logger)
    {
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Delay";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var ms = (int?)config?["ms"] ?? 250;
        if (ms < 0) ms = 0;
        if (ms > 60_000) ms = 60_000; // hard cap — anything longer is almost certainly a misconfig
        _logger.LogDebug("DelayTask: sleeping {Ms} ms", ms);
        await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
    }
}
