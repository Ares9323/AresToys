using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Loop-end marker — closes the scope opened by the nearest preceding
/// <see cref="RepeatTask"/>. Steps below the End Repeat render back at the outer indent level
/// and run ONCE per workflow execution instead of being part of the loop body. The class is a
/// no-op at runtime; <c>PipelineExecutor</c> handles scope walking when it encounters a Repeat
/// (see <c>RunRepeatBlockAsync</c>). An orphan End Repeat (no preceding Repeat in storage)
/// safely runs as a no-op and surfaces a warning banner on its step card.</summary>
public sealed class EndRepeatTask : IPipelineTask
{
    public const string TaskId = "arestoys.end-repeat";

    public string Id => TaskId;
    public string DisplayName => "End repeat";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
