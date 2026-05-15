using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.AI;
using AresToys.App.Services;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Pipeline step that traces the in-flight bytes to SVG and stows the result in
/// the bag under <c>svg_output</c>. Doesn't replace <c>payload_bytes</c> because most
/// downstream consumers expect a raster — SVG is a side-channel that <c>SaveSvgTask</c> /
/// <c>CopySvgToClipboardTask</c> picks up.
///
/// Config: <c>"preset"</c> = name of a stock or user-saved <see cref="TracePreset"/> (case-
/// insensitive lookup against <see cref="TracePresets.Stock"/> + <see cref="TracePresetStore"/>
/// custom presets). Empty / unknown name falls back to <c>[Default]</c>. The legacy
/// <c>"colors"</c> integer config is honored only when <c>"preset"</c> is absent — keeps any
/// pre-preset workflow working without migration.</summary>
public sealed class TraceToSvgTask : IPipelineTask
{
    public const string TaskId = "arestoys.trace-to-svg";
    public const string SvgOutputBagKey = "svg_output";
    public const string DefaultPresetName = "[Default]";

    private readonly IImageTracer _tracer;
    private readonly TracePresetStore _presets;
    private readonly ILogger<TraceToSvgTask> _logger;

    public TraceToSvgTask(IImageTracer tracer, TracePresetStore presets, ILogger<TraceToSvgTask> logger)
    {
        _tracer = tracer;
        _presets = presets;
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

        var presetName = (string?)config?["preset"];
        try
        {
            string? svg;
            if (!string.IsNullOrWhiteSpace(presetName))
            {
                // Stock first (in-memory, instant), then user-saved (settings DB read).
                var preset = TracePresets.Stock.FirstOrDefault(
                    p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
                if (preset is null)
                {
                    var custom = await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false);
                    preset = custom.FirstOrDefault(
                        p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
                }
                if (preset is null)
                {
                    _logger.LogWarning("TraceToSvgTask: preset '{Name}' not found; using [Default]", presetName);
                    preset = TracePresets.Stock.First(p => p.Name == DefaultPresetName);
                }
                svg = await _tracer.TraceAsync(pngBytes, preset.Options, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("TraceToSvgTask: produced {Bytes} char SVG via preset '{Preset}'",
                    svg?.Length ?? 0, preset.Name);
            }
            else
            {
                // Legacy "colors" path — kept so old workflows from before the preset selector
                // don't break. New workflows always carry "preset" (the descriptor default).
                var colors = config?["colors"]?.GetValue<int>() ?? 2;
                svg = await _tracer.TraceAsync(pngBytes, colors, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("TraceToSvgTask: produced {Bytes} char SVG via legacy colors={Colors}",
                    svg?.Length ?? 0, colors);
            }
            if (string.IsNullOrEmpty(svg)) return;
            context.Bag[SvgOutputBagKey] = svg;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TraceToSvgTask: failed");
        }
    }
}
