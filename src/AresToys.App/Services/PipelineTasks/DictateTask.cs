using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Dictation;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Dictation: records while the key that started the workflow is held, and types what was said when it is
/// released. The key is the workflow's own hotkey (a keyboard key or a mouse button), read through
/// <see cref="IWorkflowKeyHold"/>.</summary>
public sealed class DictateTask : IPipelineTask
{
    public const string TaskId = "arestoys.dictate";

    private readonly DictationService _dictation;
    private readonly IWorkflowKeyHold _keyHold;
    private readonly ILogger<DictateTask> _logger;

    public DictateTask(DictationService dictation, IWorkflowKeyHold keyHold, ILogger<DictateTask> logger)
    {
        _dictation = dictation;
        _keyHold = keyHold;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Dictate";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        if (!context.Bag.TryGetValue(PipelineBagKeys.WorkflowId, out var idObject) || idObject is not string workflowId) return;

        var isHeld = await _keyHold.GetProbeAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (isHeld is null)
        {
            // Started from a menu or by hand: there is no key to hold, so there is nothing to wait for.
            _logger.LogInformation("Dictate: workflow {Id} has no hotkey to hold", workflowId);
            return;
        }

        await _dictation.RunTakeAsync(isHeld, cancellationToken).ConfigureAwait(false);
    }
}
