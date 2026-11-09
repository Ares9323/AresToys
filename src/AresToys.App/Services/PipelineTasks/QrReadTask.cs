using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Decodes a QR code from the current image payload (<see cref="PipelineBagKeys.PayloadBytes"/>)
/// and replaces the payload with the decoded UTF-8 text. The original image is stashed under
/// <c>bag.qr_source_image</c> so a downstream "show both" task could surface it; the file
/// extension flips to <c>.txt</c> so save / clipboard / upload(text) steps treat the result as
/// text instead of an image.
///
/// Aborts the workflow when no QR is found (rather than continuing with an empty payload) so
/// the user sees an explicit "nothing to read" toast instead of an empty file landing in their
/// uploads. No per-step config today — the service hard-codes QR_CODE format + AutoRotate +
/// TryHarder, which covers the common "screenshot of a QR on screen" flow.
/// </summary>
public sealed class QrReadTask : IPipelineTask
{
    public const string TaskId = "arestoys.qr-read";

    private readonly QrReaderService _reader;
    private readonly Notifications.ToastBuilderService? _toast;
    private readonly ILogger<QrReadTask> _logger;

    public QrReadTask(QrReaderService reader, ILogger<QrReadTask> logger, Notifications.ToastBuilderService? toast = null)
    {
        _reader = reader;
        _logger = logger;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "QR — read code from image";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Bag.TryGetValue(PipelineBagKeys.PayloadBytes, out var raw) || raw is not byte[] bytes)
        {
            _logger.LogWarning("QrReadTask: bag key '{Key}' missing or not byte[]; skipping", PipelineBagKeys.PayloadBytes);
            return Task.CompletedTask;
        }
        if (bytes.Length == 0) return Task.CompletedTask;

        var text = _reader.Decode(bytes);
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogInformation("QrReadTask: no QR code recognised in the image");
            context.Abort("no QR code found");
            return Task.CompletedTask;
        }

        // Stash the source image so a future "QR + image side-by-side" step can grab it back.
        context.Bag["qr_source_image"] = bytes;
        // Replace payload with the decoded text — downstream copy / upload(text) steps see it
        // as the active payload from here on.
        var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
        context.Bag[PipelineBagKeys.PayloadBytes] = textBytes;
        context.Bag[PipelineBagKeys.FileExtension] = "txt";
        context.Bag["qr_text"] = text;
        context.Bag[PipelineBagKeys.Text] = text;

        // Critical: rewrite the staged NewItem from Image (set by the prior CaptureRegion step)
        // to Text. Without this AddToHistory persists the row as Image with text-bytes inside,
        // and NotifyToast — which routes Image-kind clicks into the editor — opens the editor
        // when the user expected the link to launch. Source stays CaptureRegion so the history
        // filter "from screen capture" still matches.
        if (context.Bag.TryGetValue(PipelineBagKeys.NewItem, out var rawItem) && rawItem is NewItem prior)
        {
            var snippet = text.Length <= 200 ? text : text[..200];
            context.Bag[PipelineBagKeys.NewItem] = prior with
            {
                Kind = ItemKind.Text,
                Payload = textBytes,
                PayloadSize = textBytes.LongLength,
                SearchText = $"QR {snippet}",
            };
        }

        _logger.LogDebug("QrReadTask: decoded {Chars} chars", text.Length);

        if ((bool?)config?["showNotification"] == true && _toast is not null)
        {
            // ToastBuilder picks Open URL (if http(s)) + Copy text + Edit (system editor)
            // automatically based on the just-rewritten NewItem text payload.
            _toast.ShowFromBag(context, (string?)config?["notificationTitle"]);
        }
        return Task.CompletedTask;
    }
}
