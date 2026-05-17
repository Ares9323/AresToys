using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;
using AresToys.Capture.Recording;
using AresToys.Core.Domain;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Items;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AresToys.App.Services.Recording;

/// <summary>Top-level orchestrator: pick region, start ffmpeg, show overlay, stop, populate the
/// pipeline bag (or, in legacy tray mode, save to history + notify toast). Mirrors the
/// capture-region flow but for video.
/// <para>
/// <b>Pipeline mode</b> (called with a non-null PipelineContext, e.g. from RecordScreenTask):
/// records into a temp folder, on stop populates bag.payload_bytes + bag.file_extension +
/// bag.new_item + bag.local_path pointing at the temp MP4. Downstream SaveVideoFile +
/// AddToHistory steps own the final disk write / history insertion / toast — same shape as
/// the image capture pipeline.
/// </para>
/// <para>
/// <b>Legacy mode</b> (called without a PipelineContext, e.g. from tray menu shortcuts):
/// preserves the old self-contained flow — writes directly to the user's capture folder,
/// AddAsync's a Video item, fires a "Recording saved" toast with Show-in-folder onClick.
/// </para></summary>
public sealed class RecordingCoordinator
{
    private const int FpsDefault = 30;
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";

    /// <summary>Temp folder under %TEMP% used by pipeline-mode recordings so the raw MP4 exists
    /// somewhere stable but doesn't pollute the user's capture folder. SaveVideoFileTask reads
    /// from here, writes to the configured destination, then deletes the temp file. Created on
    /// demand inside <see cref="ResolveOutputPath"/>.</summary>
    private static string PipelineTempFolder =>
        Path.Combine(Path.GetTempPath(), "AresToys", "recordings");

    private readonly ScreenRecordingService _recorder;
    private readonly FfmpegLocator _locator;
    private readonly FfmpegDownloader _downloader;
    private readonly IItemStore _items;
    private readonly IToastNotifier _notifier;
    private readonly AresToys.Storage.Settings.ISettingsStore _settings;
    private readonly ILogger<RecordingCoordinator> _logger;
    private RecordingOverlayWindow? _overlay;
    private RecordingFormat _activeFormat;
    private bool _downloadInProgress;
    /// <summary>True when the in-flight recording was started by a pipeline step (non-null
    /// PipelineContext). Drives <see cref="StopAndPersistAsync"/>: pipeline mode emits bag
    /// keys only and lets downstream tasks save+history+toast; legacy mode persists+toasts
    /// itself. Captured at Toggle/Start time so a subsequent stop (overlay button, second
    /// hotkey) knows which behaviour to apply.</summary>
    private bool _pipelineMode;
    /// <summary>Set when a workflow step kicks off a recording. The coordinator stashes the
    /// pipeline context here so a stop initiated from a non-pipeline path (overlay Stop button,
    /// abort, ffmpeg crash) can still populate the bag the awaiting workflow expects. Cleared
    /// after each stop. Null when the recording was started outside a workflow.</summary>
    private PipelineContext? _pendingPipelineContext;
    /// <summary>Signaled when the in-flight recording finishes (cleanly or aborted). The Start
    /// path of <see cref="ToggleAsync"/> awaits this so the workflow step doesn't return until
    /// the file is actually on disk and the bag is populated — which is what makes
    /// "Toggle screen recording → Copy file path" work in a single workflow run.</summary>
    private TaskCompletionSource<bool>? _recordingCompletion;

    public RecordingCoordinator(
        ScreenRecordingService recorder,
        FfmpegLocator locator,
        FfmpegDownloader downloader,
        IItemStore items,
        IToastNotifier notifier,
        AresToys.Storage.Settings.ISettingsStore settings,
        ILogger<RecordingCoordinator> logger)
    {
        _recorder = recorder;
        _locator = locator;
        _downloader = downloader;
        _items = items;
        _notifier = notifier;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Resolve capture folder + subfolder pattern from settings, mirroring
    /// SaveToFileTask. Recordings now land in the same folder as screenshots (typically
    /// inside the user's chosen %y\%mo subfolder) instead of the legacy hardcoded
    /// Pictures\AresToys root.</summary>
    private async Task<string> ResolveCaptureFolderAsync(CancellationToken ct)
    {
        var folderTemplate = await _settings.GetAsync(FolderSettingKey, ct).ConfigureAwait(false) ?? DefaultFolder;
        var folder = Environment.ExpandEnvironmentVariables(folderTemplate);
        var subPatternRaw = await _settings.GetAsync(SubFolderPatternSettingKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subPatternRaw))
        {
            var sub = DatePatternExpander.Expand(Environment.ExpandEnvironmentVariables(subPatternRaw), DateTime.Now);
            folder = Path.Combine(folder, sub);
        }
        return folder;
    }

    /// <summary>Strip filesystem-unsafe chars from a window title and limit length so filenames stay
    /// reasonable. Returns empty string if the input is null/whitespace.</summary>
    private static string SanitizeForFilename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '-' || c == ' ') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim('_');
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>Single hotkey toggle wrapping a recording session.
    /// <para>
    /// When called as a workflow step (<paramref name="pipelineContext"/> non-null):
    /// <list type="bullet">
    ///   <item><b>Not recording</b> → prompts for region, starts ffmpeg, then AWAITS until the
    ///         recording finishes (overlay Stop button, second-hotkey stop, abort, or ffmpeg crash).
    ///         The pipeline context is stashed in <see cref="_pendingPipelineContext"/> so whoever
    ///         triggers the stop can populate its bag — letting downstream workflow steps
    ///         (CopyText, Upload, …) see the resulting file path in a single workflow run.</item>
    ///   <item><b>Recording</b> → stops + persists into the supplied context.</item>
    /// </list>
    /// When called from a non-workflow caller (tray menu, raw hotkey outside a profile, or overlay
    /// event handler), <paramref name="pipelineContext"/> is null and the Start path still awaits
    /// the stop but no bag is populated.</para></summary>
    public async Task ToggleAsync(RecordingFormat format, CancellationToken cancellationToken, PipelineContext? pipelineContext = null)
    {
        if (_recorder.IsRecording)
        {
            // The non-overlapping case: a second hotkey press / explicit stop call. Use the
            // caller's context if present, otherwise the one captured at Start (overlay-button
            // stop pre-create on its own thread routes through here too via the event handlers
            // installed in StartAsync — those pass null and we fall back to the pending context).
            var ctx = pipelineContext ?? _pendingPipelineContext;
            await StopAndPersistAsync(cancellationToken, ctx).ConfigureAwait(false);
            return;
        }

        // First time through: stash the pipeline context for the eventual stop. Build a fresh
        // TaskCompletionSource that StopAndPersistAsync will signal once the file is on disk.
        _pendingPipelineContext = pipelineContext;
        _pipelineMode = pipelineContext is not null;
        _recordingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await StartAsync(format, cancellationToken).ConfigureAwait(false);

        // If StartAsync bailed out (user cancelled region picker, ffmpeg launch failed, etc.) the
        // overlay never came up and the recorder isn't running — release everyone immediately so
        // the calling workflow step doesn't hang forever waiting on a stop that won't come.
        if (!_recorder.IsRecording)
        {
            _recordingCompletion.TrySetResult(false);
            _pendingPipelineContext = null;
            _pipelineMode = false;
            _recordingCompletion = null;
            return;
        }

        // Block the workflow step until the recording ends. The signal can fire from any of:
        //   - StopAndPersistAsync (normal stop + abort paths)
        //   - second-hotkey re-press (routed through the IsRecording branch above)
        try { await _recordingCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* workflow cancelled — let it propagate naturally */ }
    }

    private async Task StartAsync(RecordingFormat format, CancellationToken cancellationToken)
    {
        // Ensure ffmpeg is available before we even ask the user to pick a region.
        if (_locator.Find() is null)
        {
            if (!await EnsureFfmpegInstalledAsync(cancellationToken).ConfigureAwait(false)) return;
        }

        // Re-use the region overlay we already use for screenshots, but force single-region
        // semantics: recording a multi-region capture has no use case (ffmpeg records one
        // contiguous rect), and the user reported the Enter-to-confirm step felt unnatural
        // when starting a recording. AutoConfirm = first mouse-up commits the rect.
        var region = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var overlay = new RegionOverlayWindow { AutoConfirmOnFirstSelection = true };
            return overlay.PickRegion();
        }).Task.ConfigureAwait(false);
        if (region is null) return;

        // Pipeline mode writes to a TEMP folder — the downstream SaveVideoFileTask is the one
        // that decides the user's actual destination + format. Legacy / tray mode keeps the
        // historical "same folder as screenshots" behaviour so the standalone tray menu items
        // still produce a saved file in the user's expected location.
        var folder = _pipelineMode
            ? PipelineTempFolder
            : await ResolveCaptureFolderAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var ext = format == RecordingFormat.Mp4 ? "mp4" : "gif";
        var titleSlug = SanitizeForFilename(region.WindowTitle);
        var outPath = Path.Combine(folder,
            string.IsNullOrEmpty(titleSlug) ? $"arestoys-rec-{stamp}.{ext}" : $"arestoys-rec-{titleSlug}-{stamp}.{ext}");

        var options = new RecordingOptions(
            X: region.X, Y: region.Y, Width: region.Width, Height: region.Height,
            Fps: FpsDefault, DrawCursor: true, OutputPath: outPath, Format: format);

        if (!_recorder.TryStart(options))
        {
            _notifier.Show("Recording", "FFmpeg not found. Drop ffmpeg.exe in %APPDATA%/AresToys/Tools.");
            return;
        }
        _activeFormat = format;

        // Show the overlay (red border + timer + Stop/Pause/Abort) on the captured area.
        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlay = new RecordingOverlayWindow(region.X, region.Y, region.Width, region.Height);
            _overlay.StopRequested += (_, _) => _ = StopAndPersistAsync(CancellationToken.None);
            _overlay.PauseRequested += (_, _) => { _recorder.Pause(); _overlay?.SetPausedVisual(true); };
            _overlay.ResumeRequested += (_, _) => { _recorder.Resume(); _overlay?.SetPausedVisual(false); };
            _overlay.AbortRequested += (_, _) =>
            {
                _recorder.Abort();
                _overlay?.Close();
                _overlay = null;
                _notifier.Show("Recording", "Aborted");
                // Release any workflow step that was awaiting the recording — abort = no file,
                // so we signal failure and the workflow exits with an empty bag (downstream
                // steps skip naturally because their expected keys aren't set).
                _recordingCompletion?.TrySetResult(false);
                _recordingCompletion = null;
                _pendingPipelineContext = null;
                _pipelineMode = false;
            };
            _overlay.Show();
        });
    }

    /// <summary>Returns true once ffmpeg.exe is available (existing or newly downloaded).</summary>
    private async Task<bool> EnsureFfmpegInstalledAsync(CancellationToken cancellationToken)
    {
        if (_downloadInProgress)
        {
            _notifier.Show("FFmpeg", "Download already in progress…");
            return false;
        }
        var consent = MessageBox.Show(
            "FFmpeg is required for screen recording but isn't installed yet.\n\n" +
            "Download the official build from github.com/ShareX/FFmpeg now?\n\n" +
            $"It will be installed at:\n{FfmpegLocator.ToolsFolder}",
            "Install FFmpeg",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.OK);
        if (consent != MessageBoxResult.OK) return false;

        _downloadInProgress = true;
        try
        {
            var progress = new Progress<string>(msg => _notifier.Show("FFmpeg", msg));
            _notifier.Show("FFmpeg", "Starting download…");
            var path = await _downloader.DownloadAsync(progress, cancellationToken).ConfigureAwait(false);
            if (path is null)
            {
                _notifier.Show("FFmpeg", "Download failed. Check internet connection and try again.");
                return false;
            }
            _notifier.Show("FFmpeg", "Installed. Recording is ready.");
            return true;
        }
        finally { _downloadInProgress = false; }
    }

    private async Task StopAndPersistAsync(CancellationToken cancellationToken, PipelineContext? pipelineContext = null)
    {
        // Capture the completion source up-front so any return path below (file missing,
        // exception) still releases the awaiting workflow step. Cleared from the fields here
        // so a concurrent stop call doesn't double-signal.
        var completion = _recordingCompletion;
        _recordingCompletion = null;
        var contextForBag = pipelineContext ?? _pendingPipelineContext;
        _pendingPipelineContext = null;
        var pipelineMode = _pipelineMode;
        _pipelineMode = false;

        var path = _recorder.CurrentOutputPath;
        await _recorder.StopAsync().ConfigureAwait(true);

        Application.Current.Dispatcher.Invoke(() =>
        {
            _overlay?.Close();
            _overlay = null;
        });

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _notifier.Show("Recording", "Stopped. Output file not found.");
            completion?.TrySetResult(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var ext = _activeFormat == RecordingFormat.Mp4 ? "mp4" : "gif";
        var newItem = new NewItem(
            Kind: ItemKind.Video,
            Source: ItemSource.CaptureRegion,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: bytes,
            PayloadSize: bytes.LongLength,
            BlobRef: path,
            SearchText: $"Recording {Path.GetFileName(path)}");

        if (pipelineMode && contextForBag is not null)
        {
            // Pipeline mode: emit bytes + NewItem into the bag, no history insertion, no toast.
            // Downstream SaveVideoFileTask transcodes / moves the temp file to the final
            // destination (and updates bag.local_path); AddToHistoryTask then commits the
            // item into the AresToys clipboard (it reads bag.new_item when bag.item_id is
            // absent — which is the case here since we deliberately don't AddAsync). Same
            // shape as the image-capture pipeline.
            contextForBag.Bag[PipelineBagKeys.LocalPath] = path;
            contextForBag.Bag[PipelineBagKeys.Text] = path;
            contextForBag.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            contextForBag.Bag[PipelineBagKeys.FileExtension] = ext;
            contextForBag.Bag[PipelineBagKeys.NewItem] = newItem;
            _logger.LogDebug("RecordingCoordinator: pipeline-mode stop — emitted bag (temp={Path})", path);
            completion?.TrySetResult(true);
            return;
        }

        // Legacy / tray mode: self-contained. Commit to history immediately and fire the
        // "Recording saved" toast with Show-in-folder onClick — the standalone tray menu
        // shortcuts (which can't compose a multi-step workflow) rely on this path.
        var id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
        if (contextForBag is not null)
        {
            // Defensive: a non-pipeline call that still happens to carry a context (shouldn't
            // happen with the current call sites but keep the bag populated so any caller
            // expecting the old keys doesn't break).
            contextForBag.Bag[PipelineBagKeys.LocalPath] = path;
            contextForBag.Bag[PipelineBagKeys.Text] = path;
            contextForBag.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            contextForBag.Bag[PipelineBagKeys.FileExtension] = ext;
            contextForBag.Bag[PipelineBagKeys.NewItem] = newItem;
            contextForBag.Bag[PipelineBagKeys.ItemId] = id;
        }
        completion?.TrySetResult(true);

        _notifier.Show($"Recording saved",
            Path.GetFileName(path),
            onClick: () =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            });
        _logger.LogInformation("Recording stored as item {Id} ({Format})", id, _activeFormat);
    }
}
