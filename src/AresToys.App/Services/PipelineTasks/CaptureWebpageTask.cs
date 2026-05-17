using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// First step of the webpage-capture workflow. Renders the supplied URL in a hidden WebView2
/// (via <see cref="WebpageCaptureService"/>) and stuffs the resulting full-page PNG into the bag
/// so the rest of the workflow (save / history / upload / …) is identical to the screenshot
/// pipelines.
///
/// URL resolution:
/// <list type="bullet">
///   <item>If <c>config.url</c> is set → use it directly (lets the user wire a "capture example.com
///         every Monday" workflow without a prompt).</item>
///   <item>Otherwise → show the <see cref="WebpageUrlDialog"/> to ask the user. Cancel aborts.</item>
/// </list>
///
/// Per-step config (optional):
/// <list type="bullet">
///   <item><c>url</c>: string — pre-filled or fully-automated URL.</item>
/// </list>
/// </summary>
public sealed class CaptureWebpageTask : IPipelineTask
{
    public const string TaskId = "arestoys.capture-webpage";

    private readonly WebpageCaptureService _capture;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly ILogger<CaptureWebpageTask> _logger;

    public CaptureWebpageTask(WebpageCaptureService capture, CaptureImageOutputService outputEncoder, ILogger<CaptureWebpageTask> logger)
    {
        _capture = capture;
        _outputEncoder = outputEncoder;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Capture webpage";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Bag.ContainsKey(PipelineBagKeys.PayloadBytes))
        {
            _logger.LogDebug("CaptureWebpageTask: payload already in bag; skipping capture");
            return;
        }

        // URL resolution precedence:
        //  1. Explicit step config "url" (workflow editor → wired to a static URL).
        //  2. bag.text from an upstream step (e.g. Scan QR in region → Capture webpage feeds the
        //     decoded URL straight into the page loader).
        //  3. Interactive prompt — the original on-demand flow.
        // Only fall through to the prompt when neither source provided a non-empty string.
        var configUrl = (string?)config?["url"];
        string? url = !string.IsNullOrWhiteSpace(configUrl) ? configUrl.Trim() : null;
        if (url is null
            && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText)
            && rawText is string bagText && !string.IsNullOrWhiteSpace(bagText))
        {
            url = bagText.Trim();
            _logger.LogDebug("CaptureWebpageTask: using URL from bag.text ({Url})", url);
        }
        url ??= await PromptForUrlAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("CaptureWebpageTask: no URL provided (config / bag.text / prompt all empty)");
            context.Abort("webpage capture cancelled");
            return;
        }

        var pngBytes = await _capture.CaptureAsync(url, cancellationToken).ConfigureAwait(false);
        if (pngBytes is null || pngBytes.Length == 0)
        {
            _logger.LogWarning("CaptureWebpageTask: capture returned no bytes for {Url}", url);
            context.Abort("webpage capture failed");
            return;
        }
        var (bytes, ext) = await _outputEncoder.EncodeAsync(pngBytes, cancellationToken).ConfigureAwait(false);

        context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        context.Bag[PipelineBagKeys.FileExtension] = ext;
        // Stash the URL in the window-title slot so save-to-file can use a meaningful name and
        // toast / template substitution can show what was captured.
        context.Bag[PipelineBagKeys.WindowTitle] = url;
        context.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Image,
            Source: ItemSource.CaptureWebpage,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: $"Webpage {url}");

        _logger.LogInformation("CaptureWebpageTask: captured {Url} → {Bytes} bytes", url, bytes.Length);
    }

    private static async Task<string?> PromptForUrlAsync()
    {
        // Non-modal Show() + TaskCompletionSource: ShowDialog() would put a WPF modal lock on
        // every other window in the app (Settings / popup clipboard / etc.) — the user couldn't
        // pull a URL from the AresToys clipboard popup while the prompt is up. Marshalled to the
        // UI thread because the workflow may run on a background scheduler.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new WebpageUrlDialog();
            dialog.Show();
            _ = dialog.CompletionTask.ContinueWith(t => tcs.TrySetResult(t.Result),
                TaskScheduler.Default);
        }).Task.ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }
}
