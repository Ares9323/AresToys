using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Qr;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Converts a templated text source into a QR code SVG string and writes it to
/// <c>bag.svg_output</c>. Composes with <c>SaveSvgTask</c> downstream to actually persist the
/// SVG to disk. Doesn't touch the clipboard or the history on its own — pure converter.
/// </summary>
public sealed class TextToQrSvgTask : IPipelineTask
{
    public const string TaskId = "arestoys.text-to-qr-svg";
    public const string SvgOutputBagKey = "svg_output";

    private readonly QrCodeService _qr;
    private readonly ILogger<TextToQrSvgTask> _logger;

    public TextToQrSvgTask(QrCodeService qr, ILogger<TextToQrSvgTask> logger)
    {
        _qr = qr;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Convert text to QR (SVG)";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Zero-config: read the pipeline's current text, set by the previous text-producing step.
        if (!context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) || rawText is not string text || string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("TextToQrSvgTask: bag.text empty — chain a text-producing step before this one.");
            return Task.CompletedTask;
        }

        var svg = _qr.TryRenderSvg(text);
        if (string.IsNullOrEmpty(svg))
        {
            _logger.LogWarning("TextToQrSvgTask: QR SVG rendering returned empty.");
            return Task.CompletedTask;
        }

        context.Bag[SvgOutputBagKey] = svg;
        return Task.CompletedTask;
    }
}
