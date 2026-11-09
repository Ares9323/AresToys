using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.AI;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Pipeline step that runs background removal on the in-flight image bytes. Reads
/// <see cref="PipelineBagKeys.PayloadBytes"/>, calls <see cref="IBackgroundRemover"/>, and
/// writes the alpha-masked PNG back into the same bag key plus updates
/// <see cref="PipelineBagKeys.NewItem"/> so downstream steps (AddToHistory, SaveToFile,
/// CopyImage, Upload, …) all see the cut-out version. On failure (model load fail, decode
/// fail, etc.) the original bytes pass through unchanged — the pipeline doesn't break for a
/// best-effort step.</summary>
public sealed class RemoveBackgroundTask : IPipelineTask
{
    public const string TaskId = "arestoys.remove-background";

    private readonly IBackgroundRemover _remover;
    private readonly ILogger<RemoveBackgroundTask> _logger;

    public RemoveBackgroundTask(IBackgroundRemover remover, ILogger<RemoveBackgroundTask> logger)
    {
        _remover = remover;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Remove background";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] pngBytes)
        {
            _logger.LogWarning("RemoveBackgroundTask: bag key '{Key}' missing or not byte[]; skipping",
                PipelineBagKeys.PayloadBytes);
            return;
        }
        if (pngBytes.Length == 0) return;

        try
        {
            var output = await _remover.RemoveBackgroundAsync(pngBytes, cancellationToken).ConfigureAwait(false);
            // Implementation-defined sentinel: when the remover can't run (model missing,
            // ONNX runtime broken) it returns the same buffer back. Skip the bag rewrite so
            // downstream steps keep observing the original bytes (and don't attribute a no-op
            // "removed background" to the run in logs/UI).
            if (output.Length == 0 || ReferenceEquals(output, pngBytes)) return;

            context.Bag[PipelineBagKeys.PayloadBytes] = output;
            if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem item)
            {
                context.Bag[PipelineBagKeys.NewItem] = item with
                {
                    Payload = output,
                    PayloadSize = output.LongLength,
                };
            }
            _logger.LogInformation("RemoveBackgroundTask: replaced payload ({Bytes} bytes)", output.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoveBackgroundTask: failed; keeping original bytes");
        }
    }
}
