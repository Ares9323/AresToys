using System.Text.Json.Nodes;
using AresToys.App.Services.NewPlus;
using AresToys.App.Services.Peek;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens Peek on the selection of the Explorer window in front, or retargets an open Peek to it. Does nothing
/// when the foreground window is not an Explorer window. The logic lives in <see cref="PeekService"/>; the open
/// preview window closes itself with Ctrl+Space or Esc.</summary>
public sealed class PeekTask : IPipelineTask
{
    public const string TaskId = "arestoys.peek-toggle";

    private const string ExplorerClass = "CabinetWClass";

    private readonly PeekService _service;

    public PeekTask(PeekService service)
    {
        _service = service;
    }

    public string Id => TaskId;
    public string DisplayName => "Peek";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var root = NewPlusExplorer.ForegroundRoot();
        if (NewPlusExplorer.ClassOf(root) != ExplorerClass) return;

        await _service.ToggleAsync(root).ConfigureAwait(false);
    }
}
