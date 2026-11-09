using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Wormholes;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>One task class, six WorkflowActionDescriptor rows. The descriptor's
/// <c>DefaultConfigJson</c> carries an <c>"op"</c> string that picks which manager batch
/// method runs:
/// <list type="bullet">
///   <item><c>hide-all</c> / <c>show-all</c> → <see cref="IWormholeWindowManager.SetAllHiddenAsync"/></item>
///   <item><c>lock-all</c> / <c>unlock-all</c> → <see cref="IWormholeWindowManager.SetAllLockedAsync"/></item>
///   <item><c>collapse-all</c> / <c>uncollapse-all</c> → <see cref="IWormholeWindowManager.SetAllRolledAsync"/></item>
///   <item><c>topmost-all</c> / <c>untopmost-all</c> → <see cref="IWormholeWindowManager.SetAllTopmostAsync"/></item>
/// </list>
/// Same pattern as <c>RecordScreenTask</c> (one task, two descriptor rows for mp4 vs gif) — keeps
/// the action picker friendly without spawning six near-identical task classes.</summary>
public sealed class WormholeBatchOpTask : IPipelineTask
{
    public const string TaskId = "arestoys.wormhole-batch-op";

    private readonly IWormholeWindowManager _manager;
    private readonly ILogger<WormholeBatchOpTask> _logger;

    public WormholeBatchOpTask(IWormholeWindowManager manager, ILogger<WormholeBatchOpTask> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Wormhole batch op";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var op = ((string?)config?["op"])?.ToLowerInvariant() ?? string.Empty;
        switch (op)
        {
            case "hide-all":       await _manager.SetAllHiddenAsync(true,  cancellationToken).ConfigureAwait(false); break;
            case "show-all":       await _manager.SetAllHiddenAsync(false, cancellationToken).ConfigureAwait(false); break;
            case "lock-all":       await _manager.SetAllLockedAsync(true,  cancellationToken).ConfigureAwait(false); break;
            case "unlock-all":     await _manager.SetAllLockedAsync(false, cancellationToken).ConfigureAwait(false); break;
            case "collapse-all":   await _manager.SetAllRolledAsync(true,  cancellationToken).ConfigureAwait(false); break;
            case "uncollapse-all": await _manager.SetAllRolledAsync(false, cancellationToken).ConfigureAwait(false); break;
            case "topmost-all":    await _manager.SetAllTopmostAsync(true,  cancellationToken).ConfigureAwait(false); break;
            case "untopmost-all":  await _manager.SetAllTopmostAsync(false, cancellationToken).ConfigureAwait(false); break;
            case "toggle-hide":     await _manager.ToggleAllHiddenAsync(cancellationToken).ConfigureAwait(false); break;
            case "toggle-lock":     await _manager.ToggleAllLockedAsync(cancellationToken).ConfigureAwait(false); break;
            case "toggle-collapse": await _manager.ToggleAllRolledAsync(cancellationToken).ConfigureAwait(false); break;
            case "toggle-topmost":  await _manager.ToggleAllTopmostAsync(cancellationToken).ConfigureAwait(false); break;
            default:
                _logger.LogWarning("WormholeBatchOpTask: unknown op '{Op}' — no-op", op);
                break;
        }
    }
}
