using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using AresToys.Clipboard;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Storage.Items;

namespace AresToys.App.Services;

public sealed class ClipboardIngestionService : IDisposable
{
    /// <summary>Clipboard events waiting for the worker. A burst of copies keeps the newest ones:
    /// a queued read of an older copy would only find the newer content anyway.</summary>
    private const int QueueCapacity = 8;

    /// <summary>What must be taken at the moment of the event, on the UI thread: the foreground window
    /// (shared by the gate and the reader), the capture time and the clipboard sequence number.</summary>
    private readonly record struct ClipboardJob(ForegroundSnapshot Foreground, DateTimeOffset CapturedAt, uint Sequence);

    private readonly IClipboardListener _listener;
    private readonly IClipboardReader _reader;
    private readonly IClipboardCaptureGate _gate;
    private readonly IForegroundProcessProbe _probe;
    private readonly PipelineExecutor _executor;
    private readonly IPipelineProfileStore _profiles;
    private readonly IServiceProvider _services;
    private readonly ILogger<ClipboardIngestionService> _logger;
    private readonly Channel<ClipboardJob> _queue = Channel.CreateBounded<ClipboardJob>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _ownerHwnd;

    public ClipboardIngestionService(
        IClipboardListener listener,
        IClipboardReader reader,
        IClipboardCaptureGate gate,
        IForegroundProcessProbe probe,
        PipelineExecutor executor,
        IPipelineProfileStore profiles,
        IServiceProvider services,
        ILogger<ClipboardIngestionService> logger)
    {
        _listener = listener;
        _reader = reader;
        _gate = gate;
        _probe = probe;
        _executor = executor;
        _profiles = profiles;
        _services = services;
        _logger = logger;
    }

    public void Start(IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        _ = Task.Run(() => RunWorkerAsync(_stop.Token));
        _listener.Attach(ownerHwnd);
        _listener.ClipboardUpdated += OnClipboardUpdated;
    }

    /// <summary>UI thread, once per WM_CLIPBOARDUPDATE: only the cheap facts that must be taken
    /// at once, then a job for the worker. Reading, mapping and storing happen off this thread.</summary>
    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        try
        {
            var foreground = _probe.GetForeground();
            var decision = _gate.Evaluate(foreground);
            if (!decision.Allow)
            {
                _logger.LogInformation("Clipboard event dropped by gate: {Reason}", decision.Reason);
                return;
            }

            _queue.Writer.TryWrite(new ClipboardJob(foreground, DateTimeOffset.UtcNow, ClipboardSequence.Current));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard event could not be queued");
        }
    }

    /// <summary>The one worker: drains the queue in order, one copy at a time.</summary>
    private async Task RunWorkerAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(job).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Clipboard ingestion failed");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Service disposed while waiting for the next copy.
        }
    }

    private async Task ProcessAsync(ClipboardJob job)
    {
        // A newer copy (or a self-write, which the listener muted) has replaced the clipboard since
        // this event: the newer event has its own job, and the old content is gone.
        if (job.Sequence != ClipboardSequence.Current)
        {
            _logger.LogDebug("Clipboard event skipped: superseded by a newer copy");
            return;
        }

        var change = _reader.ReadCurrent(_ownerHwnd, job.Foreground, job.CapturedAt);
        if (change is null)
        {
            _logger.LogInformation("Clipboard event dropped: reader returned null (no recognized format, over a size limit, or clipboard busy)");
            return;
        }

        _logger.LogInformation("Clipboard event captured: format={Format} payloadBytes={Bytes} source={Source}",
            change.Format, change.Payload.Length, change.SourceProcess);

        var newItem = MapToNewItem(change);
        if (newItem.Kind == ItemKind.Files && change.FilePaths is { Length: > 0 } paths)
        {
            // Image files copied in Explorer get a row thumbnail like captures do, at thumbnail size only.
            var thumbnail = ImageFiles.FileThumbnails.TryGenerate(paths);
            if (thumbnail is not null) newItem = newItem with { Thumbnail = thumbnail };
        }

        await RunPipelineAsync(newItem).ConfigureAwait(false);
    }

    private async Task RunPipelineAsync(NewItem newItem)
    {
        var profile = await _profiles.GetAsync(DefaultPipelineProfiles.OnClipboardId, CancellationToken.None).ConfigureAwait(false);
        if (profile is null)
        {
            _logger.LogWarning("on-clipboard profile missing; falling back to no-op");
            return;
        }

        var ctx = new PipelineContext(_services);
        ctx.Bag[PipelineBagKeys.NewItem] = newItem;
        await _executor.RunAsync(profile, ctx, CancellationToken.None).ConfigureAwait(false);
    }

    private static NewItem MapToNewItem(ClipboardChange change)
    {
        var (kind, payload, payloadSize, searchText) = change.Format switch
        {
            ClipboardFormat.Text => (ItemKind.Text, change.Payload, (long)change.Payload.Length, change.PreviewText),
            ClipboardFormat.Html => (ItemKind.Html, change.Payload, (long)change.Payload.Length, change.PreviewText),
            ClipboardFormat.Rtf => (ItemKind.Rtf, change.Payload, (long)change.Payload.Length, change.PreviewText),
            ClipboardFormat.Image => (ItemKind.Image, change.Payload, (long)change.Payload.Length, change.PreviewText),
            ClipboardFormat.Files => (
                ItemKind.Files,
                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(string.Join('\n', change.FilePaths ?? [])),
                (long)(change.FilePaths?.Sum(p => p.Length) ?? 0),
                change.PreviewText),
            _ => (ItemKind.Text, change.Payload, (long)change.Payload.Length, change.PreviewText)
        };

        return new NewItem(
            Kind: kind,
            Source: ItemSource.Clipboard,
            CreatedAt: change.CapturedAt,
            Payload: payload,
            PayloadSize: payloadSize,
            SourceProcess: change.SourceProcess,
            SourceWindow: change.SourceWindow,
            SearchText: searchText);
    }

    public void Dispose()
    {
        _listener.ClipboardUpdated -= OnClipboardUpdated;
        _queue.Writer.TryComplete();
        _stop.Cancel();
    }
}
