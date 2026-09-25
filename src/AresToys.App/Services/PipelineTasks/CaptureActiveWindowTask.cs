using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Capture;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// First step of the active-window workflow: snapshots the current foreground window via DWM
/// extended-frame-bounds and captures it to PNG, mirroring <see cref="CaptureRegionTask"/>'s bag
/// conventions so every downstream step (editor, save, history, upload, …) works unchanged.
///
/// We honour the global <c>capture.delay_seconds</c> setting plus a 50ms tick that gives a
/// tray-menu trigger time to dismiss its popup and let the previous foreground window regain
/// focus. Own-process windows are filtered out — useful when the user accidentally has Settings
/// or the clipboard popup focused at trigger time.
///
/// Per-step config (optional):
/// <list type="bullet">
///   <item><c>delay_seconds</c>: int — overrides the global delay (e.g. workflow-specific countdown).</item>
/// </list>
/// </summary>
public sealed class CaptureActiveWindowTask : IPipelineTask
{
    public const string TaskId = "arestoys.capture-active-window";

    private readonly ICaptureSource _captureSource;
    private readonly ISettingsStore _settings;
    private readonly CaptureImageOutputService _outputEncoder;
    private readonly ILogger<CaptureActiveWindowTask> _logger;

    private readonly IToastNotifier? _notifier;

    public CaptureActiveWindowTask(ICaptureSource captureSource, ISettingsStore settings, CaptureImageOutputService outputEncoder, ILogger<CaptureActiveWindowTask> logger, IToastNotifier? notifier = null)
    {
        _notifier = notifier;
        _captureSource = captureSource;
        _settings = settings;
        _outputEncoder = outputEncoder;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Capture active window";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Same short-circuit as CaptureRegionTask: if a tray entry pre-filled the bag we skip the
        // foreground lookup and let the rest of the workflow reuse the existing payload.
        if (context.Bag.ContainsKey(PipelineBagKeys.PayloadBytes))
        {
            _logger.LogDebug("CaptureActiveWindowTask: payload already in bag; skipping capture");
            return;
        }

        // Tray menu dismissal grace period — without it GetForegroundWindow would return AresToys
        // (the menu owner) instead of the previously-active app the user actually wants.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

        var delaySeconds = (int?)config?["delay_seconds"] ?? await ReadGlobalDelayAsync(cancellationToken).ConfigureAwait(false);
        if (delaySeconds > 0)
        {
            _logger.LogDebug("CaptureActiveWindowTask: waiting {Seconds}s before capture", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySeconds, 30)), cancellationToken).ConfigureAwait(false);
        }

        var snapshot = WindowEnumeration.GetForegroundWindowSnapshot(excludeProcessId: Environment.ProcessId);
        if (snapshot is null)
        {
            _logger.LogInformation("CaptureActiveWindowTask: no eligible foreground window — aborting workflow");
            context.Abort("no active window to capture");
            return;
        }

        var region = new CaptureRegion(snapshot.X, snapshot.Y, snapshot.Width, snapshot.Height,
            string.IsNullOrEmpty(snapshot.Title) ? "Active window" : snapshot.Title);
        // Take the previous capture's "saved" toast off the screen first, or it ends up in this shot.
        if (_notifier is not null) await _notifier.HideOnScreenPopupsAsync().ConfigureAwait(false);
        var captured = await _captureSource.CaptureAsync(region, cancellationToken).ConfigureAwait(false);
        var (bytes, ext) = await _outputEncoder.EncodeAsync(captured.PngBytes, cancellationToken).ConfigureAwait(false);

        context.Bag[PipelineBagKeys.PayloadBytes] = bytes;
        context.Bag[PipelineBagKeys.FileExtension] = ext;
        context.Bag[PipelineBagKeys.CaptureScreenPos] = (region.X, region.Y);
        if (!string.IsNullOrEmpty(region.WindowTitle))
        {
            context.Bag[PipelineBagKeys.WindowTitle] = region.WindowTitle;
        }
        if (WindowEnumeration.GetForegroundProcessName(Environment.ProcessId) is { } appName)
        {
            context.Bag[PipelineBagKeys.AppName] = appName;
        }
        var searchText = string.IsNullOrEmpty(region.WindowTitle) ? "Active window" : region.WindowTitle!;
        context.Bag[PipelineBagKeys.NewItem] = new NewItem(
            Kind: ItemKind.Image,
            Source: ItemSource.CaptureWindow,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            SearchText: $"{searchText} {captured.Width}×{captured.Height}");

        _logger.LogInformation("CaptureActiveWindowTask: captured '{Title}' at ({X}, {Y}) {W}×{H} px",
            snapshot.Title, region.X, region.Y, region.Width, region.Height);
    }

    private async Task<int> ReadGlobalDelayAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync("capture.delay_seconds", cancellationToken).ConfigureAwait(false);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
