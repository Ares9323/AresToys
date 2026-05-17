using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Qr;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Converts a templated text source into a QR code PNG and writes it to the bag, mirroring what
/// a capture task does: <c>bag.payload_bytes</c> = PNG bytes, <c>bag.file_extension="png"</c>,
/// <c>bag.new_item</c> = staged Image. The user then composes with downstream sinks
/// ("Add image", "Save as Image file", etc.) — this task itself does not touch any clipboard.
/// </summary>
public sealed class TextToQrPngTask : IPipelineTask
{
    public const string TaskId = "arestoys.text-to-qr-png";

    private readonly QrCodeService _qr;
    private readonly ILogger<TextToQrPngTask> _logger;

    public TextToQrPngTask(QrCodeService qr, ILogger<TextToQrPngTask> logger)
    {
        _qr = qr;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Convert text to QR (PNG)";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Zero-config: read the pipeline's current text, set by the previous text-producing step.
        if (!context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) || rawText is not string text || string.IsNullOrEmpty(text))
        {
            _logger.LogDebug("TextToQrPngTask: bag.text empty — chain a text-producing step before this one.");
            return Task.CompletedTask;
        }

        var png = _qr.TryRenderPng(text);
        if (png is null || png.Length == 0)
        {
            _logger.LogWarning("TextToQrPngTask: QR rendering returned empty for {Chars} chars of input.", text.Length);
            return Task.CompletedTask;
        }

        // Stage into the standard capture-style bag keys so downstream sinks (Add image, Save
        // as Image file) treat this exactly the same as a fresh screenshot would.
        context.Bag[PipelineBagKeys.PayloadBytes] = png;
        context.Bag[PipelineBagKeys.FileExtension] = "png";
        context.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Image,
            Source: ItemSource.Pipeline,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: png,
            PayloadSize: png.LongLength,
            SearchText: "QR: " + (text.Length <= 80 ? text : text[..80] + "…"));
        return Task.CompletedTask;
    }

}
