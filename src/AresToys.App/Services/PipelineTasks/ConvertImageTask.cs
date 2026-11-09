using System.Text.Json.Nodes;
using AresToys.App.Services.ImageResize;
using AresToys.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Re-encodes the image in <see cref="PipelineBagKeys.PayloadBytes"/> to another format
/// (format, quality and strip_metadata config keys) without changing its size.</summary>
public sealed class ConvertImageTask : IPipelineTask
{
    public const string TaskId = "arestoys.convert-image";

    private readonly ILogger<ConvertImageTask> _logger;

    public ConvertImageTask(ILogger<ConvertImageTask> logger)
    {
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Convert image";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ResizeImageTask.Apply(context, null, ImageEncodeOptions.FromConfig(config), _logger, DisplayName);
        return Task.CompletedTask;
    }
}
