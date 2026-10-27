using System.Text.Json.Nodes;
using AresToys.App.Services.Zones;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Opens the Window snapping layout editor, or brings the open one to the front.</summary>
public sealed class OpenWindowSnappingEditorTask : IPipelineTask
{
    public const string TaskId = "arestoys.window-snapping-editor";

    private readonly WindowSnappingService _service;

    public OpenWindowSnappingEditorTask(WindowSnappingService service)
    {
        _service = service;
    }

    public string Id => TaskId;
    public string DisplayName => "Window snapping editor";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        _service.OpenEditor();
        return Task.CompletedTask;
    }
}
