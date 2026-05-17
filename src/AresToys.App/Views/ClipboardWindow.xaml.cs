using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AresToys.App.ViewModels;
using AresToys.Core.Domain;
using AresToys.Storage.Settings;

namespace AresToys.App.Views;

/// <summary>The Win+V clipboard window — search, categories, history list, preview and
/// per-item commands all driven by <see cref="PopupWindowViewModel"/> (kept under the legacy
/// name during the popup→clipboard migration). Built on <see cref="Wpf.Ui.Controls.FluentWindow"/>
/// like the rest of the app (Mica/DWM caption, edge-resize, dark titlebar handled natively)
/// — was previously a custom-chrome Window with AllowsTransparency=True + manual Thumb resize,
/// which produced ghosting on resize and worse perf.</summary>
public partial class ClipboardWindow : Wpf.Ui.Controls.FluentWindow
{
    private static ClipboardWindow? _current;
    public static bool IsOpen => _current is { IsLoaded: true, IsVisible: true };
    public static void RequestClose() => _current?.BeginHide();

    /// <summary>HWND of the live ClipboardWindow, or <see cref="IntPtr.Zero"/> when no popup is
    /// open. Used by <c>TargetWindowTracker</c> to distinguish "the popup itself" from "another
    /// AresToys window the user was typing into" (e.g. WebpageUrlDialog) — only the popup is
    /// excluded from being a paste target.</summary>
    public static IntPtr CurrentHwnd
    {
        get
        {
            if (_current is null) return IntPtr.Zero;
            try { return new System.Windows.Interop.WindowInteropHelper(_current).Handle; }
            catch { return IntPtr.Zero; }
        }
    }

    private const string SizeWidthKey   = "clipboard.size.width";
    private const string SizeHeightKey  = "clipboard.size.height";
    private const string PreviewWidthKey = "clipboard.preview.width";
    private const string PositionLeftKey = "clipboard.position.left";
    private const string PositionTopKey  = "clipboard.position.top";

    private readonly ISettingsStore _settings;
    private bool _isClosing;
    /// <summary>Set true at the end of <see cref="OnLoaded"/>. Persistence handlers
    /// (size/location/preview) bail out until then so the WPF layout pass and the
    /// initial restore don't overwrite the user's last saved values.</summary>
    private bool _geometryRestored;
    /// <summary>True while the popup spawns an in-process child window (QR generator, etc.)
    /// that takes focus. The Deactivated handler reads this and skips its auto-hide pass —
    /// without the gate the popup would slam shut as soon as the QR window stole focus.
    /// Mirrors the launcher's _suppressDeactivation pattern.</summary>
    private bool _suppressDeactivation;

    // RMB-pan state for the image preview ScrollViewer. Mirrors the editor canvas pan: hold
    // RMB to drag the scroll offsets, release to stop. Cursor flips to SizeAll while panning
    // and restores on release.
    private bool _isPreviewPanning;
    private System.Windows.Point _previewPanStartCursor;
    private double _previewPanStartScrollH;
    private double _previewPanStartScrollV;
    private Cursor? _previewPanSavedCursor;

    public PopupWindowViewModel ViewModel { get; }

    private readonly AresToys.Storage.Rotation.CategoryRotationService? _categoryRotation;
    private readonly AresToys.App.Services.Qr.QrCodeService? _qrService;
    private readonly AresToys.App.Services.ManualUploadService? _ingestion;

    public ClipboardWindow(PopupWindowViewModel viewModel, ISettingsStore settings, AresToys.Storage.Rotation.CategoryRotationService? categoryRotation = null, AresToys.App.Services.Qr.QrCodeService? qrService = null, AresToys.App.Services.ManualUploadService? ingestion = null)
    {
        InitializeComponent();
        AresToys.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        AresToys.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        _settings = settings;
        _categoryRotation = categoryRotation;
        _qrService = qrService;
        _ingestion = ingestion;
        _current = this;

        // Hydrate the persisted "mute preview videos" preference so the first MediaElement load
        // applies it without flicker. Fire-and-forget — the ApplyPreviewMutedToControls call
        // tolerates being run before the visual tree is wired up (null-guards everywhere).
        _ = LoadPreviewMutedPreferenceAsync();

        // Tunneling so Ctrl+digits / arrows / Enter reach this handler before SearchBox
        // (or any other focused TextBox) swallows them.
        PreviewKeyDown += OnKeyDown;
        HistoryList.MouseDoubleClick += OnHistoryDoubleClick;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Hide on every paste path (Enter / Ctrl+digits / toolbar button) — the VM raises
        // PasteCompleted after AutoPaster finishes, regardless of which surface invoked it.
        viewModel.PasteCompleted += OnPasteCompleted;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        // The window is registered AddSingleton, so the X button on the FluentWindow titlebar
        // (which calls Close()) would tear down the singleton instance and the next Win+V
        // press would throw "Cannot Show after a Window has closed". Intercept the close,
        // cancel it, and Hide instead — matches Esc / paste-completion flow. App shutdown
        // closes via Application.Current.Shutdown() which doesn't go through this event.
        Closing += OnClosing;
        // Click-outside / alt-tab dismiss. Skipped when pinned (sticky mode) or when a child
        // window of ours is taking focus (QR generator). Mirrors the launcher's behaviour.
        Deactivated += OnDeactivated;
        // GridSplitter drags don't trigger the window's SizeChanged — listen on the preview
        // pane itself so we capture both window resizes and splitter drags.
        PreviewPane.SizeChanged += OnPreviewPaneSizeChanged;

        // Sync-only handler: data hydration lives in PrepareAsync (called by hosts BEFORE Show).
        // Previously this method did the full SQLite query + Rows.Clear+refill AFTER the window
        // was already visible, which produced a brief stale-then-empty-then-fresh flash. The
        // VM's ItemsChanged subscription keeps Rows live while hidden, so PrepareAsync usually
        // skips the query entirely (version-skip path).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            _isClosing = false;
            Focus();
        };
    }

    /// <summary>Hosts await this before <c>Show()</c>. Composes:
    /// <list type="bullet">
    /// <item>Throttled per-category cleanup sweep — skipped when the previous sweep was less
    /// than 15s ago (the rotation scheduler's regular tick is 30s, so we don't need a write
    /// every open). Eliminates the redundant SQLite write when the user reopens the window
    /// rapidly (Win+V, Esc, Win+V).</item>
    /// <item>VM <c>PrepareAsync</c>: one-shot type-filter hydration + version-gated refresh.</item>
    /// </list></summary>
    public async Task PrepareAsync()
    {
        // Cleanup sweep throttle — skip when the last sweep was recent. The 30s scheduler tick
        // already covers cold periods; this just avoids the per-open write hit.
        if (_categoryRotation is not null && (DateTime.UtcNow - _lastCategorySweep).TotalSeconds > 15)
        {
            try
            {
                await _categoryRotation.RunAsync(CancellationToken.None).ConfigureAwait(true);
                _lastCategorySweep = DateTime.UtcNow;
            }
            catch { /* sweep is best-effort — Refresh shows whatever survived */ }
        }
        await ViewModel.PrepareAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private DateTime _lastCategorySweep = DateTime.MinValue;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-navigate the WebBrowser to a dark blank page so MSHTML doesn't flash white
        // about:blank before the first real preview lands.
        try { HtmlPreviewBox.NavigateToString(WrapWithCharset(string.Empty)); }
        catch { /* MSHTML may not be initialized yet — first real navigation will set the bg */ }

        try
        {
            var w = await _settings.GetAsync(SizeWidthKey,  CancellationToken.None).ConfigureAwait(true);
            var h = await _settings.GetAsync(SizeHeightKey, CancellationToken.None).ConfigureAwait(true);
            if (w is not null && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && h is not null && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                Width  = Math.Max(MinWidth,  width);
                Height = Math.Max(MinHeight, height);
            }

            var pw = await _settings.GetAsync(PreviewWidthKey, CancellationToken.None).ConfigureAwait(true);
            if (pw is not null && double.TryParse(pw, NumberStyles.Float, CultureInfo.InvariantCulture, out var previewWidth))
            {
                PreviewColumn.Width = new GridLength(Math.Max(PreviewColumn.MinWidth, previewWidth), GridUnitType.Pixel);
            }

            // Restore last position if we saved one. Validate against the virtual screen so a
            // disconnected monitor between sessions doesn't open the window off-screen — fall
            // back to centering in that case.
            var lx = await _settings.GetAsync(PositionLeftKey, CancellationToken.None).ConfigureAwait(true);
            var ly = await _settings.GetAsync(PositionTopKey,  CancellationToken.None).ConfigureAwait(true);
            if (lx is not null && double.TryParse(lx, NumberStyles.Float, CultureInfo.InvariantCulture, out var savedLeft)
                && ly is not null && double.TryParse(ly, NumberStyles.Float, CultureInfo.InvariantCulture, out var savedTop)
                && IsOnVirtualScreen(savedLeft, savedTop))
            {
                Left = savedLeft;
                Top  = savedTop;
            }
            else
            {
                // No (or invalid) saved position → center on the primary work area.
                Left = (SystemParameters.WorkArea.Width  - ActualWidth)  / 2 + SystemParameters.WorkArea.Left;
                Top  = (SystemParameters.WorkArea.Height - ActualHeight) / 2 + SystemParameters.WorkArea.Top;
            }
        }
        catch { /* settings unavailable — keep default geometry */ }
        _geometryRestored = true;
    }

    private static bool IsOnVirtualScreen(double left, double top)
    {
        var rect = new Rect(left, top, 40, 40);
        var virt = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virt.IntersectsWith(rect);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_geometryRestored) return;
        _ = _settings.SetAsync(SizeWidthKey,
            ActualWidth.ToString(CultureInfo.InvariantCulture),  sensitive: false, CancellationToken.None);
        _ = _settings.SetAsync(SizeHeightKey,
            ActualHeight.ToString(CultureInfo.InvariantCulture), sensitive: false, CancellationToken.None);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_geometryRestored) return;
        _ = _settings.SetAsync(PositionLeftKey,
            Left.ToString(CultureInfo.InvariantCulture), sensitive: false, CancellationToken.None);
        _ = _settings.SetAsync(PositionTopKey,
            Top.ToString(CultureInfo.InvariantCulture),  sensitive: false, CancellationToken.None);
    }

    private void OnPreviewPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_geometryRestored) return;
        if (!e.WidthChanged) return;
        _ = _settings.SetAsync(PreviewWidthKey,
            PreviewPane.ActualWidth.ToString(CultureInfo.InvariantCulture),
            sensitive: false, CancellationToken.None);
    }

    /// <summary>X-button / Alt+F4 → Hide the singleton instead of letting WPF Close() it.
    /// Once a Window is closed it can't be shown again, and the next Win+V press would throw
    /// "Cannot Show after a Window has closed". The IsShuttingDown gate lets the real
    /// Close go through during app exit (WPF closes every window during shutdown).</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsShuttingDown) return;
        e.Cancel = true;
        BeginHide();
    }

    /// <summary>Auto-dismiss when the user clicks outside the popup (the standard Win+V
    /// behaviour). Skipped in three cases:
    ///   • Pinned mode — sticky stays open until the user explicitly toggles or Win+V again.
    ///   • _suppressDeactivation — set around in-process child-window launches (QR generator).
    ///   • _isClosing — guard against re-entry while BeginHide is mid-flight.
    /// App shutdown also bypasses since IsShuttingDown closes everything anyway.</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (App.IsShuttingDown) return;
        if (_isClosing) return;
        if (_suppressDeactivation) return;
        if (ViewModel.IsPinned) return;
        BeginHide();
    }

    private void OnPasteCompleted(object? sender, EventArgs e)
    {
        // Pinned mode keeps the popup open across pastes for multi-paste workflows. AutoPaster
        // just transferred foreground to the target and sent Ctrl+V; without grabbing focus
        // back, the next Ctrl+digit / Enter would land on the target instead of the popup.
        // The grab needs a brief delay (~80 ms) so the Ctrl+V keystrokes already in the
        // target's input queue process before we steal the foreground — otherwise a fast
        // sequence (Ctrl+1, Ctrl+2) can race and skip the second paste.
        if (ViewModel.IsPinned)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(80).ConfigureAwait(true);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) AresToys.App.Services.TargetWindowTracker.ForceForeground(hwnd);
            }, DispatcherPriority.Background);
            return;
        }
        // AutoPaster's SetForegroundWindow has already handed focus to the target by the time
        // this fires, so simply hiding here is safe — we're no longer the foreground window
        // and Win32's anti-focus-stealing rules don't kick in.
        // PRIORITY=Send (sync-on-dispatcher, not deferred to Background): the popup stays
        // Topmost until Hide() runs, and a deferred Hide leaves a ~1-frame window where the
        // popup can still intercept keystrokes via its PreviewKeyDown — consuming the user's
        // first keystroke after paste before the target window's TextBox can see it.
        Dispatcher.BeginInvoke(new Action(BeginHide), DispatcherPriority.Send);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PopupWindowViewModel.PreviewRtfBytes):
                // Defer until after layout: by then the Visibility binding driven by
                // IsRtfPreview has flipped the RichTextBox to Visible, so loading the
                // document actually paints.
                Dispatcher.BeginInvoke(
                    new Action(() => LoadRtfPreview(ViewModel.PreviewRtfBytes)),
                    DispatcherPriority.ContextIdle);
                break;
            case nameof(PopupWindowViewModel.PreviewHtml):
                Dispatcher.BeginInvoke(
                    new Action(() => LoadHtmlPreview(ViewModel.PreviewHtml)),
                    DispatcherPriority.ContextIdle);
                break;
            case nameof(PopupWindowViewModel.PreviewImageBytes):
                // Reset scroll, then size the LayoutTransform so the new image fits the pane
                // (no upscale beyond 1:1 — small images stay at native size and don't blur).
                // Ctrl+wheel can override afterwards. Defer to ContextIdle so the ScrollViewer
                // has its real ViewportWidth/Height by the time we read them.
                if (PreviewImageScroller is not null)
                {
                    PreviewImageScroller.ScrollToHorizontalOffset(0);
                    PreviewImageScroller.ScrollToVerticalOffset(0);
                }
                Dispatcher.BeginInvoke(
                    new Action(FitPreviewImageToPane),
                    DispatcherPriority.ContextIdle);
                break;
            case nameof(PopupWindowViewModel.PreviewVideoPath):
                // Imperatively set the MediaElement source — binding Source directly to a
                // changing path string is unreliable in WPF (stale handles, no clear file lock
                // release). Stop + null first so the previous file unlocks, then re-Open.
                if (PreviewVideoPlayer is not null)
                {
                    StopPreviewVideoTimer();
                    PreviewVideoPlayer.Stop();
                    PreviewVideoPlayer.Close();
                    PreviewVideoPlayer.Source = null;
                    var path = ViewModel.PreviewVideoPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        PreviewVideoPlayer.Source = new Uri(path, UriKind.Absolute);
                        PreviewVideoPlayer.Play();
                        UpdatePlayPauseGlyph(playing: true);
                        // Slider + timer are wired up by OnPreviewVideoOpened once decoding
                        // surfaces the duration metadata — nothing to start manually here.
                    }
                    else
                    {
                        UpdatePlayPauseGlyph(playing: false);
                    }
                }
                break;
        }
    }

    /// <summary>Polls <c>MediaElement.Position</c> ~7×/sec to keep the seek slider + timecode in
    /// sync with playback. Started on MediaOpened, stopped on Closed / failed / MediaEnded
    /// (well, briefly — we restart it via the loop). Slower than the per-frame rate to keep CPU
    /// in single digits even on long recordings.</summary>
    private DispatcherTimer? _previewVideoTimer;
    /// <summary>Guard that prevents the slider's ValueChanged handler from feeding our own
    /// timer-driven updates back into <c>MediaElement.Position</c> — that would create a
    /// jittery feedback loop and stutter the playback.</summary>
    private bool _previewVideoSliderUpdating;
    private bool _previewVideoPlaying;

    /// <summary>Once the MediaElement has decoded enough of the source to know its duration, we
    /// can size the seek slider and start the timer that drives it during playback.</summary>
    private void OnPreviewVideoOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewVideoPlayer is null) return;
        // Re-apply the global mute preference every time we open a new clip — MediaElement
        // resets IsMuted to false on Open.
        ApplyPreviewMutedToControls();
        var duration = PreviewVideoPlayer.NaturalDuration.HasTimeSpan
            ? PreviewVideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        if (PreviewVideoSlider is not null)
        {
            _previewVideoSliderUpdating = true;
            PreviewVideoSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001);
            PreviewVideoSlider.Value = 0;
            _previewVideoSliderUpdating = false;
        }
        UpdatePreviewVideoTimeText(TimeSpan.Zero, duration);

        _previewVideoTimer?.Stop();
        _previewVideoTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _previewVideoTimer.Tick += OnPreviewVideoTimerTick;
        _previewVideoTimer.Start();

        UpdatePlayPauseGlyph(playing: PreviewVideoPlayer.Source is not null);
    }

    private void OnPreviewVideoTimerTick(object? sender, EventArgs e)
    {
        if (PreviewVideoPlayer is null || PreviewVideoSlider is null) return;
        var pos = PreviewVideoPlayer.Position;
        var duration = PreviewVideoPlayer.NaturalDuration.HasTimeSpan
            ? PreviewVideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        _previewVideoSliderUpdating = true;
        PreviewVideoSlider.Value = pos.TotalSeconds;
        _previewVideoSliderUpdating = false;
        UpdatePreviewVideoTimeText(pos, duration);
    }

    private void OnPreviewVideoSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Two callers possible: the timer above (which sets _previewVideoSliderUpdating=true so we
        // bail), or the user dragging / clicking the track (we treat that as a seek).
        if (_previewVideoSliderUpdating) return;
        if (PreviewVideoPlayer is null) return;
        PreviewVideoPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
        if (PreviewVideoPlayer.NaturalDuration.HasTimeSpan)
            UpdatePreviewVideoTimeText(PreviewVideoPlayer.Position, PreviewVideoPlayer.NaturalDuration.TimeSpan);
    }

    private void UpdatePreviewVideoTimeText(TimeSpan position, TimeSpan duration)
    {
        if (PreviewVideoTimeText is null) return;
        PreviewVideoTimeText.Text = $"{FormatVideoTime(position)} / {FormatVideoTime(duration)}";
    }

    private static string FormatVideoTime(TimeSpan t)
    {
        // mm:ss is fine for the recording lengths AresToys produces (seconds to a few minutes).
        // Switch to h:mm:ss if a clip happens to break the hour mark.
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>Loop the video by seeking back to zero and resuming. Matches the inline-preview
    /// UX of Telegram / WhatsApp / Discord: a recording previewed in the popup should keep
    /// playing as long as the user is looking at it.</summary>
    private void OnPreviewVideoEnded(object sender, RoutedEventArgs e)
    {
        if (PreviewVideoPlayer is null) return;
        PreviewVideoPlayer.Position = TimeSpan.Zero;
        PreviewVideoPlayer.Play();
        UpdatePlayPauseGlyph(playing: true);
    }

    private async void OnPreviewVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // MediaElement loads through Windows Media Foundation. Common reasons it fails:
        //   - Win10 N / KN edition without the Media Feature Pack (no built-in mp4/h264).
        //   - File moved or locked by another writer.
        //   - Exotic codec the OS doesn't know.
        // Fall back to the still-frame ffmpeg thumbnail when we can — the user at least sees
        // what the recording looks like. If even that fails (no ffmpeg installed) the slot
        // shows a short explanatory line so the user knows it's not just a blank panel.
        System.Diagnostics.Debug.WriteLine($"Preview video failed: {e.ErrorException.Message}");
        StopPreviewVideoTimer();

        var path = ViewModel.PreviewVideoPath;
        var selectedId = ViewModel.SelectedRow?.Id;
        if (!string.IsNullOrEmpty(path) && selectedId is { } id)
        {
            var thumbSvc = ((App)System.Windows.Application.Current).Services.GetService(typeof(AresToys.App.Services.Recording.VideoThumbnailService))
                as AresToys.App.Services.Recording.VideoThumbnailService;
            if (thumbSvc is not null)
            {
                var thumb = await thumbSvc.GenerateAsync(id, path, CancellationToken.None);
                if (thumb is { Length: > 0 })
                {
                    // Hand off to the existing image-preview channel: the MediaElement is hidden
                    // because IsVideoPreview flips back to false when we switch kinds.
                    ViewModel.PreviewImageBytes = thumb;
                    ViewModel.PreviewVideoPath = null;
                    ViewModel.PreviewKind = AresToys.App.ViewModels.PreviewKind.Image;
                    return;
                }
            }
        }
        // No ffmpeg / no thumbnail available — surface a plain-text explanation in the text
        // preview slot so the pane isn't just a blank rectangle.
        ViewModel.PreviewVideoPath = null;
        ViewModel.PreviewText = "Preview unavailable. " +
            "Windows Media Foundation couldn't decode this file — common on Win10/11 N or KN editions without the Media Feature Pack. " +
            "Ctrl+V still works in apps that handle the file directly.";
        ViewModel.PreviewKind = AresToys.App.ViewModels.PreviewKind.Text;
    }

    private void OnPreviewVideoSurfaceClick(object sender, MouseButtonEventArgs e)
    {
        TogglePreviewVideoPlayback();
        e.Handled = true;
    }

    private void OnPreviewVideoPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        TogglePreviewVideoPlayback();
    }

    private void TogglePreviewVideoPlayback()
    {
        if (PreviewVideoPlayer is null) return;
        if (_previewVideoPlaying)
        {
            PreviewVideoPlayer.Pause();
            UpdatePlayPauseGlyph(playing: false);
        }
        else
        {
            PreviewVideoPlayer.Play();
            UpdatePlayPauseGlyph(playing: true);
        }
    }

    private void UpdatePlayPauseGlyph(bool playing)
    {
        _previewVideoPlaying = playing;
        if (PreviewVideoPlayPauseButton is not null)
        {
            // Square (U+25A0) = playing (click to pause), triangle (U+25B6) = paused (click to play).
            // Plain Symbols-block glyphs avoid the rendering issues of the dedicated media-control
            // codepoints in the fallback font chain.
            PreviewVideoPlayPauseButton.Content = playing ? "■" : "▶";
        }
    }

    private void StopPreviewVideoTimer()
    {
        if (_previewVideoTimer is null) return;
        _previewVideoTimer.Stop();
        _previewVideoTimer.Tick -= OnPreviewVideoTimerTick;
        _previewVideoTimer = null;
    }

    private const string PreviewMutedSettingKey = "clipboard.preview.muted";
    /// <summary>Cached mute preference. Hydrated once at construction (see ClipboardWindow ctor)
    /// from the settings store; every subsequent toggle persists immediately so the choice rides
    /// across selections AND app restarts.</summary>
    private bool _previewMuted = true; // default to muted — quieter UX for screen recordings.

    private async Task LoadPreviewMutedPreferenceAsync()
    {
        try
        {
            var store = ((App)System.Windows.Application.Current).Services.GetService(
                typeof(AresToys.Storage.Settings.ISettingsStore)) as AresToys.Storage.Settings.ISettingsStore;
            if (store is null) return;
            var raw = await store.GetAsync(PreviewMutedSettingKey, CancellationToken.None).ConfigureAwait(true);
            // Default = muted (true). Only "0" / "false" flips it off — anything else (including
            // the unset / first-launch case) keeps the silent default.
            _previewMuted = raw != "0" && !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
            ApplyPreviewMutedToControls();
        }
        catch { /* settings store hiccup — keep the silent default */ }
    }

    private void ApplyPreviewMutedToControls()
    {
        if (PreviewVideoPlayer is not null) PreviewVideoPlayer.IsMuted = _previewMuted;
        if (PreviewVideoMuteButton is not null)
            PreviewVideoMuteButton.Content = _previewMuted ? "🔇" : "🔊";
    }

    private async void OnPreviewVideoMuteClicked(object sender, RoutedEventArgs e)
    {
        _previewMuted = !_previewMuted;
        ApplyPreviewMutedToControls();
        try
        {
            var store = ((App)System.Windows.Application.Current).Services.GetService(
                typeof(AresToys.Storage.Settings.ISettingsStore)) as AresToys.Storage.Settings.ISettingsStore;
            if (store is not null)
                await store.SetAsync(PreviewMutedSettingKey, _previewMuted ? "1" : "0", sensitive: false, CancellationToken.None);
        }
        catch { /* persistence failure isn't fatal — in-memory state still reflects the toggle */ }
    }

    /// <summary>Wheel handling on the image preview. Ctrl+wheel zooms the image; Shift+wheel
    /// scrolls horizontally (standard Windows convention — WPF's ScrollViewer doesn't translate
    /// the modifier itself). Bare wheel falls through to the ScrollViewer for vertical scroll.
    /// Zoom range 0.1×..8× matches the PinnedImageWindow envelope so the two surfaces feel
    /// the same.</summary>
    private void OnPreviewImageWheel(object sender, MouseWheelEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl)
        {
            var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var newScale = Math.Clamp(PreviewImageScale.ScaleX * factor, 0.1, 8.0);
            if (Math.Abs(newScale - PreviewImageScale.ScaleX) < 1e-4) return;
            PreviewImageScale.ScaleX = newScale;
            PreviewImageScale.ScaleY = newScale;
            e.Handled = true;
            return;
        }

        if (shift)
        {
            // ~16px per logical scroll line × the user's WheelScrollLines preference (default 3).
            var step = SystemParameters.WheelScrollLines * 16;
            var direction = e.Delta > 0 ? -1 : 1;
            PreviewImageScroller.ScrollToHorizontalOffset(PreviewImageScroller.HorizontalOffset + direction * step);
            e.Handled = true;
        }
    }

    /// <summary>Compute the LayoutTransform scale that makes the current image fit the
    /// preview pane and apply it. Decoding the bytes a second time (the converter already
    /// produced a BitmapImage for the visual tree) is cheap for clipboard payloads and avoids
    /// having to wait for the Image element's ActualWidth/Height after layout.</summary>
    private void FitPreviewImageToPane()
    {
        if (PreviewImageScale is null || PreviewImageScroller is null) return;
        var bytes = ViewModel.PreviewImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            PreviewImageScale.ScaleX = 1.0;
            PreviewImageScale.ScaleY = 1.0;
            return;
        }
        int w, h;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            w = bmp.PixelWidth;
            h = bmp.PixelHeight;
        }
        catch
        {
            PreviewImageScale.ScaleX = 1.0;
            PreviewImageScale.ScaleY = 1.0;
            return;
        }
        if (w <= 0 || h <= 0) return;
        // Subtract the Image's 8-px margin on each side so the fit isn't clipped by it.
        var vw = PreviewImageScroller.ViewportWidth - 20;
        var vh = PreviewImageScroller.ViewportHeight - 20;
        if (vw <= 0 || vh <= 0) return;
        var fit = Math.Min(vw / w, vh / h);
        // Never upscale on auto-fit — small images stay 1:1, large ones shrink to fit.
        if (fit > 1.0) fit = 1.0;
        if (fit < 0.1) fit = 0.1;
        PreviewImageScale.ScaleX = fit;
        PreviewImageScale.ScaleY = fit;
    }

    /// <summary>Start MMB-pan on the image preview — ShareX-parity (was RMB pre-0.1.6).
    /// Captures the cursor + scroll offsets so MouseMove can translate the scroll, swaps
    /// the cursor to SizeAll for feedback. Mouse-capture keeps MouseMove firing if the user
    /// drags outside the ScrollViewer bounds. Wired through generic PreviewMouseDown +
    /// ChangedButton check because WPF doesn't expose a dedicated MiddleButton variant.</summary>
    private void OnPreviewImagePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (PreviewImageScroller is null) return;
        // Skip when the click lands on a scrollbar — let it keep its own MMB semantics.
        if (e.OriginalSource is DependencyObject src && IsOnScrollBar(src)) return;
        _isPreviewPanning = true;
        _previewPanStartCursor = e.GetPosition(PreviewImageScroller);
        _previewPanStartScrollH = PreviewImageScroller.HorizontalOffset;
        _previewPanStartScrollV = PreviewImageScroller.VerticalOffset;
        _previewPanSavedCursor = PreviewImageScroller.Cursor;
        PreviewImageScroller.Cursor = Cursors.SizeAll;
        PreviewImageScroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPreviewPanning) return;
        if (e.MiddleButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(PreviewImageScroller);
        var dx = current.X - _previewPanStartCursor.X;
        var dy = current.Y - _previewPanStartCursor.Y;
        PreviewImageScroller.ScrollToHorizontalOffset(_previewPanStartScrollH - dx);
        PreviewImageScroller.ScrollToVerticalOffset(_previewPanStartScrollV - dy);
        e.Handled = true;
    }

    private void OnPreviewImagePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_isPreviewPanning) return;
        _isPreviewPanning = false;
        PreviewImageScroller.ReleaseMouseCapture();
        PreviewImageScroller.Cursor = _previewPanSavedCursor;
        _previewPanSavedCursor = null;
        e.Handled = true;
    }

    private static bool IsOnScrollBar(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ScrollBar) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node)
                   ?? (node is FrameworkElement fe ? fe.Parent : null);
        }
        return false;
    }

    private void LoadRtfPreview(byte[]? rtf)
    {
        var doc = new FlowDocument();
        if (rtf is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(rtf);
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                range.Load(ms, DataFormats.Rtf);
            }
            catch
            {
                // Malformed RTF — leave the document empty.
            }
        }
        RtfPreviewBox.Document = doc;
    }

    private void LoadHtmlPreview(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            HtmlPreviewBox.NavigateToString(WrapWithCharset(string.Empty));
            return;
        }
        var trimmed = StripCfHtmlPreamble(html);
        try { HtmlPreviewBox.NavigateToString(trimmed); }
        catch { /* Some HTML payloads break MSHTML — render nothing rather than crash. */ }
    }

    /// <summary>Clipboard HTML usually arrives wrapped in CF_HTML metadata; strip everything
    /// outside &lt;!--StartFragment--&gt; / &lt;!--EndFragment--&gt; when present so the
    /// WebBrowser sees only the visible markup.</summary>
    private static string StripCfHtmlPreamble(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        var startMarker = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        if (startMarker >= 0)
        {
            startMarker += "<!--StartFragment-->".Length;
            var endMarker = html.IndexOf("<!--EndFragment-->", startMarker, StringComparison.OrdinalIgnoreCase);
            if (endMarker < 0) endMarker = html.Length;
            return WrapWithCharset(html[startMarker..endMarker]);
        }

        var firstTag = html.IndexOf('<');
        if (firstTag > 0)
        {
            var preamble = html[..firstTag];
            if (preamble.Contains("Version:", StringComparison.OrdinalIgnoreCase)
                || preamble.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase)
                || preamble.Contains("StartFragment:", StringComparison.OrdinalIgnoreCase))
            {
                return InjectCharsetMeta(html[firstTag..]);
            }
        }

        return InjectCharsetMeta(html);
    }

    private static string WrapWithCharset(string innerHtml)
        => $"<html><head><meta charset=\"utf-8\">{DarkThemeStyle}</head><body>{innerHtml}</body></html>";

    private const string DarkThemeStyle =
        "<style>" +
        "html,body{background:#1E1E1E;color:#DDD;font-family:Segoe UI,sans-serif;font-size:13px;margin:8px;}" +
        "a{color:#7FB3FF;}" +
        "table,td,th{border-color:#444;}" +
        "</style>";

    private static string InjectCharsetMeta(string html)
    {
        if (html.Contains("<meta charset", StringComparison.OrdinalIgnoreCase)) return html;
        var headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            var headClose = html.IndexOf('>', headIdx);
            if (headClose >= 0)
            {
                return html[..(headClose + 1)] + "<meta charset=\"utf-8\">" + DarkThemeStyle + html[(headClose + 1)..];
            }
        }
        var htmlIdx = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlIdx >= 0)
        {
            var htmlClose = html.IndexOf('>', htmlIdx);
            if (htmlClose >= 0)
            {
                return html[..(htmlClose + 1)] + $"<head><meta charset=\"utf-8\">{DarkThemeStyle}</head>" + html[(htmlClose + 1)..];
            }
        }
        return WrapWithCharset(html);
    }

    /// <summary>Right-clicking a row should both select it and open its context menu.
    /// WPF doesn't auto-select on right-click, so the move-to command would otherwise act
    /// on whichever row was previously selected.</summary>
    private void OnItemRowPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        if (b.DataContext is ItemRowViewModel row)
        {
            HistoryList.SelectedItem = row;
        }
    }

    // ── Drag-to-category: drop a row onto a category tab header → move it ────────────
    // Custom format keeps the payload distinct from FileDrop / text drags so a drop from
    // Explorer or another app on a category tab is silently ignored.
    private const string ClipboardItemDragFormat = "arestoys.clipboard.item";
    private System.Windows.Point? _rowDragStart;
    private long _rowDragSourceId;

    private void OnItemRowPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.DataContext is not ItemRowViewModel row)
        {
            _rowDragStart = null;
            return;
        }
        // Don't arm a drag while the row is in inline-rename mode — left-clicks there belong
        // to the TextBox caret, not the drag gesture.
        if (row.IsRenaming) { _rowDragStart = null; return; }
        // Skip drag-arming when the click originated on an interactive child (chevron
        // reorder buttons). Without this check, clicking ↑/↓ would also start a drag if the
        // user moved the mouse even slightly before releasing — wrong gesture.
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
        {
            _rowDragStart = null;
            return;
        }
        _rowDragStart = e.GetPosition(this);
        _rowDragSourceId = row.Id;
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    private void OnItemRowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rowDragStart is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _rowDragStart = null; return; }
        var pos = e.GetPosition(this);
        var dx = Math.Abs(pos.X - _rowDragStart.Value.X);
        var dy = Math.Abs(pos.Y - _rowDragStart.Value.Y);
        // Honour the OS-defined drag threshold so a click without movement (select / paste)
        // doesn't get reinterpreted as a drag and steal the click semantics.
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        var sourceId = _rowDragSourceId;
        _rowDragStart = null;
        if (sender is not Border b) return;
        var data = new DataObject(ClipboardItemDragFormat, sourceId);
        // Move semantics — the item leaves the current category and enters the target.
        DragDrop.DoDragDrop(b, data, DragDropEffects.Move);
    }

    private void OnCategoryTabDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ClipboardItemDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (sender is FrameworkElement fe) fe.Opacity = 0.6;
        e.Handled = true;
    }

    private void OnCategoryTabDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Opacity = 1.0;
    }

    // ── Pinned reorder: chevron clicks + drag-onto-row ───────────────────────────────

    private async void OnRowMoveUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ItemRowViewModel row || !row.Pinned) return;
        await ViewModel.MovePinnedAsync(row.Id, -1);
    }

    private async void OnRowMoveDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ItemRowViewModel row || !row.Pinned) return;
        await ViewModel.MovePinnedAsync(row.Id, +1);
    }

    private void OnItemRowDragEnter(object sender, DragEventArgs e)
    {
        if (!IsValidPinnedReorderDrag(sender, e, out _, out _))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (sender is Border b) b.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBackgroundBrush");
        e.Handled = true;
    }

    private void OnItemRowDragLeave(object sender, DragEventArgs e)
    {
        // Reset BorderBrush to whatever the row Style would have applied. The selection
        // trigger re-evaluates on leave; for non-selected rows we go back to Transparent.
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
    }

    private async void OnItemRowDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
        if (!IsValidPinnedReorderDrag(sender, e, out var sourceId, out var targetId))
        {
            return;
        }
        e.Handled = true;
        await ViewModel.ReorderPinnedAsync(sourceId, targetId);
    }

    /// <summary>Common gate for the pinned-reorder DnD. Allows the operation only when both
    /// the source row (carried in the DataObject) and the target row (the Border's
    /// DataContext) are pinned and different from each other. Drops between unpinned rows
    /// or from unpinned to pinned (and vice-versa) are silently ignored — those gestures
    /// have no meaningful reorder semantic.</summary>
    private bool IsValidPinnedReorderDrag(object sender, DragEventArgs e, out long sourceId, out long targetId)
    {
        sourceId = 0;
        targetId = 0;
        if (!e.Data.GetDataPresent(ClipboardItemDragFormat)) return false;
        if (e.Data.GetData(ClipboardItemDragFormat) is not long sid) return false;
        if (sender is not Border border) return false;
        if (border.DataContext is not ItemRowViewModel targetRow) return false;
        if (!targetRow.Pinned) return false;
        var sourceRow = ViewModel.Rows.FirstOrDefault(r => r.Id == sid);
        if (sourceRow is null || !sourceRow.Pinned) return false;
        if (sourceRow.Id == targetRow.Id) return false;
        sourceId = sid;
        targetId = targetRow.Id;
        return true;
    }

    private async void OnCategoryTabDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Opacity = 1.0;
        if (!e.Data.GetDataPresent(ClipboardItemDragFormat)) return;
        if (e.Data.GetData(ClipboardItemDragFormat) is not long itemId) return;
        // The category name lives in the Button.Tag (bound to CategoryTab.Name in XAML) so
        // we don't depend on DataContext lookup — works regardless of which inner element
        // of the button template actually received the drop.
        if (sender is not FrameworkElement target || target.Tag is not string categoryName) return;
        if (string.IsNullOrEmpty(categoryName)) return;
        // Skip the round-trip when the drop target IS the source's current category — avoids
        // an unnecessary UPDATE + ItemsChanged broadcast for a no-op.
        var current = ViewModel.Rows.FirstOrDefault(r => r.Id == itemId);
        if (current is null) return;
        if (string.Equals(ViewModel.ActiveCategory, categoryName, StringComparison.Ordinal)) return;
        e.Handled = true;
        await ViewModel.MoveItemToCategoryAsync(itemId, categoryName);
    }

    private void OnHistoryDoubleClick(object? sender, MouseButtonEventArgs e)
    {
        // Double-click an image row → open editor; double-click a text row → paste.
        if (ViewModel.SelectedRow is not { } row) return;
        if (row.Kind == ItemKind.Image)
        {
            ViewModel.OpenInEditorCommand.Execute(null);
        }
        else
        {
            ViewModel.PasteSelectedCommand.Execute(null);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Bail-out for label-edit TextBoxes (inline rename + preview pane). PreviewKeyDown
        // tunnels DOWN through the visual tree, so without this check Enter → PasteSelected
        // would fire before the TextBox's own KeyDown handler ever sees the keystroke, and
        // the label commit would be replaced by a paste. SearchBox is intentionally exempt:
        // there Enter / Up / Down ARE the intended shortcuts (search → arrow → Enter pastes).
        if (Keyboard.FocusedElement is TextBox focusedTb && focusedTb != SearchBox)
        {
            // Allow Esc to bubble to the TextBox's own KeyDown so rename cancels properly,
            // but don't let the window's Esc → BeginHide path fire. Everything else
            // (Enter, Delete, F2, Up/Down, etc.) is left for the TextBox to handle.
            return;
        }

        // Ctrl+Shift+Del — wipe everything. Highlighted in the footer hint so users can find it.
        if (e.Key == Key.Delete &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ViewModel.ClearAllCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+1..9: quick-paste row N (1-indexed). Tunneled so SearchBox doesn't eat the digit.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            var idx = e.Key - Key.D1;
            if (idx >= 0 && idx < ViewModel.Rows.Count)
            {
                ViewModel.SelectedRow = ViewModel.Rows[idx];
                ViewModel.PasteSelectedCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // Plain 1..9 (top-row or NumPad, no modifiers) — focus the matching row in the
        // active category. Skipped when the search box has focus so digits stay typeable
        // inside the query. Out-of-range digits (e.g. "5" with only 3 items) are no-ops.
        if (Keyboard.Modifiers == ModifierKeys.None && !IsSearchBoxFocused())
        {
            var plainIdx = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                _ => -1,
            };
            if (plainIdx >= 0)
            {
                if (plainIdx < ViewModel.Rows.Count)
                {
                    ViewModel.SelectedRow = ViewModel.Rows[plainIdx];
                    ScrollSelectedIntoView();
                }
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape) { BeginHide(); e.Handled = true; return; }

        // Ctrl+F — focus search.
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+P — toggle pin on selected item.
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ViewModel.TogglePinSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                ViewModel.MoveSelectionCommand.Execute(1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.MoveSelectionCommand.Execute(-1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Right:
                // Arrows cycle through category tabs; skip when search has focus so the user
                // can still move the caret inside their query.
                if (IsSearchBoxFocused()) return;
                SwitchCategory(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (ViewModel.SelectedRow is not null)
                {
                    // Shift+Enter = "paste the path as text" (when the row has an on-disk file —
                    // Image / Video / Files saved by Save-to-file / recording / AddFile). Falls
                    // back to the normal paste behaviour automatically when the path can't be
                    // resolved, so the user always gets some paste action.
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        ViewModel.PasteSelectedAsPathCommand.Execute(null);
                    else
                        ViewModel.PasteSelectedCommand.Execute(null);
                }
                e.Handled = true;
                break;
            case Key.Delete:
                // Don't hijack Delete inside the SearchBox — let it edit characters there.
                if (IsSearchBoxFocused()) return;
                ViewModel.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                // F2 on the selected row = start inline-rename of its label. Skipped when the
                // search box has focus so the user can keep typing freely. Already-renaming is
                // a no-op (the IsRenaming setter early-returns when the value doesn't change).
                if (IsSearchBoxFocused()) return;
                if (ViewModel.SelectedRow is { } row) row.IsRenaming = true;
                e.Handled = true;
                break;
        }
    }

    private bool IsSearchBoxFocused() => SearchBox.IsKeyboardFocusWithin;

    private async void SwitchCategory(int delta)
    {
        var tabs = ViewModel.Categories;
        if (tabs.Count == 0) return;
        var current = -1;
        for (var i = 0; i < tabs.Count; i++) if (tabs[i].IsActive) { current = i; break; }
        if (current < 0) current = 0;
        var next = (current + delta + tabs.Count) % tabs.Count;
        ViewModel.SelectCategoryCommand.Execute(tabs[next].Name);

        // Wait one dispatcher tick so RefreshAsync inside the VM finishes repopulating Rows
        // before we focus the first one.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (ViewModel.Rows.Count > 0)
        {
            ViewModel.SelectedRow = ViewModel.Rows[0];
            ScrollSelectedIntoView();
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (ViewModel.SelectedRow is null) return;
        HistoryList.ScrollIntoView(ViewModel.SelectedRow);
    }

    /// <summary>Hide on the next dispatcher cycle so the current event handler can fully
    /// unwind first. Hide() (not Close()) because the window is registered Singleton — the
    /// next Show reuses the same instance for an instant reopen.</summary>
    private void BeginHide()
    {
        if (_isClosing) return;
        _isClosing = true;
        // Hide immediately (Send priority) rather than at Background — when invoked from
        // OnPasteCompleted, the Topmost popup must clear the visual + input layer before the
        // user's next keystroke, otherwise the first post-paste key lands on the popup's
        // PreviewKeyDown and gets swallowed without effect. For the Esc / Deactivated paths the
        // priority bump just means hide happens one dispatcher pump earlier — no visible change.
        Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Send);
    }

    /// <summary>Pinned mode toggle in the titlebar — flips the VM flag (which persists via
    /// the OnIsPinnedChanged partial) and updates the button caption via the Style DataTrigger.
    /// Mirrors the launcher's "Drag mode" toggle pattern.</summary>
    private void OnPinnedToggleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPinned = !ViewModel.IsPinned;
    }

    /// <summary>"Generate QR code…" right-click action — pre-fills the live generator with
    /// the selected row's preview text. Preview is truncated at 200 chars in the row VM,
    /// which is fine for URLs / short snippets; longer payloads (full vCards, JSON blobs)
    /// the user can paste manually into the editor.</summary>
    private void OnGenerateQrFromItemClicked(object sender, RoutedEventArgs e)
    {
        if (_qrService is null) return;
        if (ViewModel.SelectedRow is not { } row) return;
        // Non-text rows produce placeholder previews like "[image]" — pop the editor empty
        // in that case rather than seeding it with junk.
        var initial = row.Kind == ItemKind.Image || row.Kind == ItemKind.Video || row.Kind == ItemKind.Files
            ? null
            : row.Preview;
        var win = new QrGeneratorWindow(_qrService, initial, _settings, _ingestion) { Owner = this };
        // Keep the popup open while the QR generator is up — the focus shift to the child
        // window would otherwise trip OnDeactivated and slam the clipboard popup shut.
        // Cleared on the QR window's Closed event so a normal click-outside afterwards still
        // dismisses the popup as before.
        _suppressDeactivation = true;
        win.Closed += (_, _) => _suppressDeactivation = false;
        win.Show();
        win.Activate();
    }

    // ── Label edit: inline rename + preview-pane TextBox ─────────────────────────────

    /// <summary>Right-click → "Rename label" menu handler. <see cref="OnItemRowPreviewRightClick"/>
    /// already selected the clicked row before the menu opens (WPF doesn't auto-select on
    /// right-click), so we just flip the selected row into rename mode here.</summary>
    private void OnRenameLabelClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRow is { } row) row.IsRenaming = true;
    }

    /// <summary>Auto-focus + select-all every time the rename TextBox transitions from hidden
    /// to visible. Loaded fires once per TextBox instance (template materialisation) and would
    /// miss subsequent rename invocations on the same row; IsVisibleChanged fires every
    /// IsRenaming flip, which is what we actually want — Explorer-style F2 rename behaviour.</summary>
    private void OnRenameTextBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.NewValue is not true) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }), DispatcherPriority.Background);
    }

    private async void OnRenameTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not ItemRowViewModel row) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // Commit the typed text. NormalizeLabel inside the store handles empty → null
            // (= "clear the label, go back to showing the snippet"), trim, and the 200-char cap.
            row.IsRenaming = false;
            await ViewModel.CommitLabelAsync(row.Id, tb.Text);
            // Hand focus back to the list so the next keystroke (arrows / Enter / etc.) goes
            // where the user expects.
            HistoryList.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            row.IsRenaming = false;
            HistoryList.Focus();
        }
    }

    /// <summary>LostFocus = cancel for the inline rename — the user clicked away, treat it as
    /// "I changed my mind" and revert. Only Enter commits. Symmetrical to Esc.</summary>
    private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not ItemRowViewModel row) return;
        // The row VM's Label setter hasn't been touched (we bind OneWay), so flipping IsRenaming
        // off simply restores the previous TextBlock view.
        row.IsRenaming = false;
    }

    /// <summary>Preview-pane label TextBox — Enter commits + drops focus (so the next arrow
    /// keypress reaches the list). Esc reverts pending edits and drops focus without
    /// committing. The LostFocus handler also commits; Enter just short-circuits via blur.</summary>
    private async void OnPreviewLabelKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (ViewModel.SelectedRow is not { } row) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ViewModel.CommitLabelAsync(row.Id, tb.Text);
            HistoryList.Focus();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Reset text to the saved value so LostFocus sees equality and skips the UPDATE,
            // then drop focus. A second Esc press (now with focus outside) closes the window.
            tb.Text = row.Label ?? string.Empty;
            HistoryList.Focus();
        }
    }

    /// <summary>LostFocus = commit for the preview pane — the TextBox is the natural resting
    /// target there (always-on for the selected row, not a transient edit mode), so the user
    /// clicking away means "I'm done typing, save it". Inverse of the inline rename.</summary>
    private async void OnPreviewLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (ViewModel.SelectedRow is not { } row) return;
        // Skip the commit when the text hasn't actually changed — avoids a redundant SQLite
        // UPDATE + ItemsChanged broadcast every time the user clicks elsewhere.
        var current = row.Label ?? string.Empty;
        if (string.Equals(tb.Text ?? string.Empty, current, StringComparison.Ordinal)) return;
        await ViewModel.CommitLabelAsync(row.Id, tb.Text);
    }

    /// <summary>Char-level filter on the Trigger TextBox: drop any keystroke that would add a
    /// character outside [a-zA-Z0-9_]. Stops the user from typing invalid chars in the first
    /// place instead of validating at commit time. Combined with <see cref="OnPreviewTriggerPasting"/>
    /// it also rejects pasted strings containing invalid chars (the storage layer normalises
    /// whitespace to null but doesn't enforce the regex — validation is a UI concern).</summary>
    private void OnPreviewTriggerTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
        {
            if (!IsTriggerChar(c))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void OnPreviewTriggerPasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string))) { e.CancelCommand(); return; }
        var pasted = (string)e.DataObject.GetData(typeof(string))!;
        foreach (var c in pasted)
        {
            if (!IsTriggerChar(c))
            {
                e.CancelCommand();
                return;
            }
        }
    }

    private static bool IsTriggerChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    /// <summary>Preview-pane Trigger TextBox — same commit/cancel semantics as the Label box.
    /// Enter commits and blurs; Esc reverts to the stored value and blurs; LostFocus commits if
    /// the text differs. Trigger content is validated at the storage layer (whitespace → null,
    /// no length check beyond the 64-char MaxLength on the TextBox itself).</summary>
    private async void OnPreviewTriggerKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (ViewModel.SelectedRow is not { } row) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ViewModel.CommitTriggerAsync(row.Id, tb.Text);
            HistoryList.Focus();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            tb.Text = row.Trigger ?? string.Empty;
            HistoryList.Focus();
        }
    }

    private async void OnPreviewTriggerLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (ViewModel.SelectedRow is not { } row) return;
        var current = row.Trigger ?? string.Empty;
        if (string.Equals(tb.Text ?? string.Empty, current, StringComparison.Ordinal)) return;
        await ViewModel.CommitTriggerAsync(row.Id, tb.Text);
    }
}
