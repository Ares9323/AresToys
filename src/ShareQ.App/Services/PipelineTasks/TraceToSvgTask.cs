using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ShareQ.AI;
using ShareQ.Core.Pipeline;

namespace ShareQ.App.Services.PipelineTasks;

/// <summary>Pipeline step that traces the in-flight bytes to SVG and stows the result in
/// the bag under <c>svg_output</c>. Doesn't replace <c>payload_bytes</c> because most
/// downstream consumers expect a raster — SVG is a side-channel that a future
/// <c>SaveSvgTask</c> / <c>CopySvgToClipboardTask</c> can pick up. Config:
/// <c>"colors"</c> integer (default 2 = monochrome).</summary>
public sealed class TraceToSvgTask : IPipelineTask
{
    public const string TaskId = "shareq.trace-to-svg";
    public const string SvgOutputBagKey = "svg_output";

    private readonly IImageTracer _tracer;
    private readonly ILogger<TraceToSvgTask> _logger;

    public TraceToSvgTask(IImageTracer tracer, ILogger<TraceToSvgTask> logger)
    {
        _tracer = tracer;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Trace to SVG";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] pngBytes)
        {
            _logger.LogWarning("TraceToSvgTask: bag key '{Key}' missing or not byte[]; skipping",
                PipelineBagKeys.PayloadBytes);
            return;
        }
        if (pngBytes.Length == 0) return;

        var colors = config?["colors"]?.GetValue<int>() ?? 2;
        try
        {
            var svg = await _tracer.TraceAsync(pngBytes, colors, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(svg)) return;
            context.Bag[SvgOutputBagKey] = svg;
            _logger.LogInformation("TraceToSvgTask: produced {Bytes} char SVG", svg.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TraceToSvgTask: failed");
        }
    }
}
