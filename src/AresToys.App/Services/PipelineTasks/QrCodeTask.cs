using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Qr;
using AresToys.App.Views;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Generates a QR code from <c>bag.upload_url</c> (or <c>config.text</c>) and pops up
/// a small window showing it. Also writes the rendered PNG to <c>bag.payload_bytes</c> +
/// sets <c>bag.file_extension</c> = "png" so downstream tasks (Save image as…, Copy image
/// to clipboard, Add to history, Upload) can chain off the same image without re-rendering.</summary>
public sealed class QrCodeTask : IPipelineTask
{
    public const string TaskId = "arestoys.show-qr-code";

    private readonly ILogger<QrCodeTask> _logger;
    private readonly QrCodeService _qr;

    public QrCodeTask(ILogger<QrCodeTask> logger, QrCodeService qr)
    {
        _logger = logger;
        _qr = qr;
    }

    public string Id => TaskId;
    public string DisplayName => "Show QR code";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Resolution order: hardcoded config.text > bag.text (Upload / Shorten URL / Read
        // clipboard / Convert / etc.). Legacy bag.upload_url removed in 0.1.17 — Upload now
        // writes its URL into bag.text directly.
        var text = (string?)config?["text"]
                   ?? (context.Bag.TryGetValue(PipelineBagKeys.Text, out var raw) && raw is string u ? u : null);
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogWarning("QrCodeTask: no text in config or bag.text; skipping");
            return Task.CompletedTask;
        }

        var pngBytes = _qr.TryRenderPng(text);
        if (pngBytes is null) return Task.CompletedTask;

        // Make the rendered PNG available to whatever the user chains after — Save image as…,
        // Copy image to clipboard, Add to history. Without this they'd have to re-render via
        // a Generate-QR task.
        context.Bag[PipelineBagKeys.PayloadBytes] = pngBytes;
        context.Bag[PipelineBagKeys.FileExtension] = "png";

        var bitmap = _qr.TryRenderBitmap(text);
        if (bitmap is null) return Task.CompletedTask;

        // Show the window on the UI thread without awaiting — workflow continues immediately.
        // No Owner: when the workflow is triggered from tray / hotkey the MainWindow is usually
        // hidden, and an Owner-bound child gets positioned relative to that hidden window
        // (typically off-screen). Independent + Topmost + CenterScreen + Activate keeps the QR
        // visible even when nothing else of AresToys is on screen.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var window = new QrCodeWindow(bitmap, text);
            window.Show();
            window.Activate();
        });
        return Task.CompletedTask;
    }
}
