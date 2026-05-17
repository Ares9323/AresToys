using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Loop marker — every step BELOW this one is re-executed N times by the
/// <c>PipelineExecutor</c>'s special-case handling. The class itself is a no-op at runtime
/// (the executor intercepts the task-id before invoking <see cref="ExecuteAsync"/>); it exists
/// so DI registration + the workflow editor's "+ Add step" picker have a real
/// <see cref="IPipelineTask"/> to surface.
/// <para>
/// Config keys:
/// <list type="bullet">
/// <item><c>count</c> (int, default 5, clamped 1–1000) — how many times to repeat the
/// following block.</item>
/// <item><c>delayMs</c> (int, default 0) — milliseconds to sleep between iterations. Useful
/// for "spam Ctrl+W every 200 ms" type macros.</item>
/// <item><c>cancelCombo</c> (string, optional) — global hotkey combo (Ctrl+Shift+X format)
/// that breaks out of the loop early. The editor's combo-capture button populates this. Leave
/// empty and the loop runs to completion (or until the workflow's outer cancellation token
/// fires).</item>
/// </list>
/// </para></summary>
public sealed class RepeatTask : IPipelineTask
{
    public const string TaskId = "arestoys.repeat";

    public string Id => TaskId;
    public string DisplayName => "Repeat next steps";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    /// <summary>Intentionally empty — the executor handles repeat by inspecting the step's
    /// TaskId before invoking ExecuteAsync, so this method is never actually called in
    /// production. Returns immediately as a defensive no-op for the (unlikely) case where a
    /// custom executor doesn't recognise the repeat task id.</summary>
    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
