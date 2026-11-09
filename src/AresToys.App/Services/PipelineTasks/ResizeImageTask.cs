using System.IO;
using System.Text.Json.Nodes;
using AresToys.App.Services.ImageResize;
using AresToys.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Resizes the image in <see cref="PipelineBagKeys.PayloadBytes"/> and optionally
/// re-encodes it. Reads the step config keys documented on <see cref="ImageResizeOptions"/> and
/// <see cref="ImageEncodeOptions"/>; the work itself lives in <see cref="ImageResizeEngine"/>.
/// A missing or undecodable payload leaves the bag untouched.</summary>
public sealed class ResizeImageTask : IPipelineTask
{
    public const string TaskId = "arestoys.resize-image";

    private readonly ILogger<ResizeImageTask> _logger;

    public ResizeImageTask(ILogger<ResizeImageTask> logger)
    {
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Resize image";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        Apply(context, ImageResizeOptions.FromConfig(config), ImageEncodeOptions.FromConfig(config), _logger, DisplayName);
        return Task.CompletedTask;
    }

    /// <summary>Runs the engine over the bag payload and writes back the bytes together with the
    /// extension that matches them. Shared with <see cref="ConvertImageTask"/>, which passes no
    /// resize options.</summary>
    internal static void Apply(PipelineContext context, ImageResizeOptions? resize, ImageEncodeOptions encode, ILogger logger, string taskName)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] input)
        {
            logger.LogDebug("{Task}: no PayloadBytes in bag, skipping", taskName);
            return;
        }

        try
        {
            var output = ImageResizeEngine.Process(input, resize, encode, out var extension);
            context.Bag[PipelineBagKeys.PayloadBytes] = output;
            context.Bag[PipelineBagKeys.FileExtension] = extension;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "{Task}: payload is not a decodable image, leaving the bag unchanged", taskName);
        }
    }
}
