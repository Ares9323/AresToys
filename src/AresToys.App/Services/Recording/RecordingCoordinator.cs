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

/// <summary>Top-level orchestrator: pick region, start ffmpeg, show overlay, stop, save to history,
/// notify toast (click → open in folder). Mirrors the capture-region flow but for video.</summary>
public sealed class RecordingCoordinator
{
    private const int FpsDefault = 30;
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";

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
        _recordingCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await StartAsync(format, cancellationToken).ConfigureAwait(false);

        // If StartAsync bailed out (user cancelled region picker, ffmpeg launch failed, etc.) the
        // overlay never came up and the recorder isn't running — release everyone immediately so
        // the calling workflow step doesn't hang forever waiting on a stop that won't come.
        if (!_recorder.IsRecording)
        {
            _recordingCompletion.TrySetResult(false);
            _pendingPipelineContext = null;
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

        // Same folder + subfolder-pattern resolution as SaveToFileTask, so screen recordings
        // land alongside screenshots (typically the same %y\%mo subfolder) instead of in the
        // legacy hardcoded Pictures\AresToys root.
        var folder = await ResolveCaptureFolderAsync(cancellationToken).ConfigureAwait(false);
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

        var id = await _items.AddAsync(newItem, cancellationToken).ConfigureAwait(false);

        // Populate the pipeline bag when this stop is happening as part of a workflow run.
        // Mirrors the bag keys the capture tasks (CaptureRegion / CaptureActiveWindow / etc.) set
        // so any downstream task that works after a screenshot will also work after a recording
        // — CopyTextToClipboardTask with template "{bag.local_path}", UploadTask reading
        // PayloadBytes + FileExtension, AddToHistoryTask consuming NewItem, etc.
        if (contextForBag is not null)
        {
            contextForBag.Bag[PipelineBagKeys.LocalPath] = path;
            contextForBag.Bag[PipelineBagKeys.Text] = path;
            contextForBag.Bag[PipelineBagKeys.PayloadBytes] = bytes;
            contextForBag.Bag[PipelineBagKeys.FileExtension] = ext;
            contextForBag.Bag[PipelineBagKeys.NewItem] = newItem;
            contextForBag.Bag[PipelineBagKeys.ItemId] = id;
            _logger.LogDebug("RecordingCoordinator: populated pipeline bag — local_path={Path} item_id={Id}", path, id);
        }

        // Release the awaiting workflow step (if any). True = clean stop with a valid file
        // on disk; downstream steps can rely on bag.local_path / bag.new_item being set IF
        // contextForBag was provided.
        completion?.TrySetResult(true);

        // When the recording was kicked off from a workflow step, RecordScreenTask owns the
        // post-stop UX (showNotification toggle + ToastBuilder). Skipping the legacy toast here
        // avoids a double-notification stack. UI-only stops (overlay button outside any
        // workflow, no context captured) still get the historical notify so the user has
        // immediate feedback.
        if (contextForBag is null)
        {
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
        }
        _logger.LogInformation("Recording stored as item {Id} ({Format})", id, _activeFormat);
    }
}
