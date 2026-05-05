using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ShareQ.App.ViewModels;
using ShareQ.Core.Domain;
using ShareQ.Storage.Settings;

namespace ShareQ.App.Views;

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

    // RMB-pan state for the image preview ScrollViewer. Mirrors the editor canvas pan: hold
    // RMB to drag the scroll offsets, release to stop. Cursor flips to SizeAll while panning
    // and restores on release.
    private bool _isPreviewPanning;
    private System.Windows.Point _previewPanStartCursor;
    private double _previewPanStartScrollH;
    private double _previewPanStartScrollV;
    private Cursor? _previewPanSavedCursor;

    public PopupWindowViewModel ViewModel { get; }

    private readonly ShareQ.Storage.Rotation.CategoryRotationService? _categoryRotation;
    private readonly ShareQ.App.Services.Qr.QrCodeService? _qrService;
    private readonly ShareQ.App.Services.ManualUploadService? _ingestion;

    public ClipboardWindow(PopupWindowViewModel viewModel, ISettingsStore settings, ShareQ.Storage.Rotation.CategoryRotationService? categoryRotation = null, ShareQ.App.Services.Qr.QrCodeService? qrService = null, ShareQ.App.Services.ManualUploadService? ingestion = null)
    {
        InitializeComponent();
        ShareQ.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        ShareQ.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        _settings = settings;
        _categoryRotation = categoryRotation;
        _qrService = qrService;
        _ingestion = ingestion;
        _current = this;

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
        // GridSplitter drags don't trigger the window's SizeChanged — listen on the preview
        // pane itself so we capture both window resizes and splitter drags.
        PreviewPane.SizeChanged += OnPreviewPaneSizeChanged;

        // Refresh data every time the window becomes visible (Singleton lifetime — Loaded
        // fires once, IsVisibleChanged fires on every Show).
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is not true) return;
            _isClosing = false;
            // On-open immediate sweep: enforce per-category MaxItems + AutoCleanupAfter before
            // we paint the list, so a "1-minute cleanup" item that's already expired doesn't
            // flash onscreen for the gap between Now and the next 30s scheduler tick. Best-
            // effort — failures fall through to whatever the scheduler's next tick produces.
            if (_categoryRotation is not null)
            {
                try { await _categoryRotation.RunAsync(CancellationToken.None).ConfigureAwait(true); }
                catch { /* sweep is fire-and-forget — Refresh below shows whatever survived */ }
            }
            await ViewModel.RefreshAsync(CancellationToken.None);
            Focus();
        };
    }

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

    private void OnPasteCompleted(object? sender, EventArgs e)
    {
        // AutoPaster's SetForegroundWindow has already handed focus to the target by the time
        // this fires, so simply hiding here is safe — we're no longer the foreground window
        // and Win32's anti-focus-stealing rules don't kick in.
        Dispatcher.BeginInvoke(new Action(BeginHide), DispatcherPriority.Background);
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
        }
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

    /// <summary>Start RMB-pan on the image preview — same gesture the editor canvas uses.
    /// Captures the cursor + scroll offsets so MouseMove can translate the scroll, swaps
    /// the cursor to SizeAll for feedback. Mouse-capture keeps MouseMove firing if the user
    /// drags outside the ScrollViewer bounds.</summary>
    private void OnPreviewImageRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImageScroller is null) return;
        // Skip when the click lands on a scrollbar — let it keep its own RMB semantics.
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
        if (e.RightButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(PreviewImageScroller);
        var dx = current.X - _previewPanStartCursor.X;
        var dy = current.Y - _previewPanStartCursor.Y;
        PreviewImageScroller.ScrollToHorizontalOffset(_previewPanStartScrollH - dx);
        PreviewImageScroller.ScrollToVerticalOffset(_previewPanStartScrollV - dy);
        e.Handled = true;
    }

    private void OnPreviewImageRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPreviewPanning) return;
        _isPreviewPanning = false;
        PreviewImageScroller.ReleaseMouseCapture();
        PreviewImageScroller.Cursor = _previewPanSavedCursor;
        _previewPanSavedCursor = null;
        // Mark handled so the right-button release doesn't bubble up to a context menu.
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
                    ViewModel.PasteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                // Don't hijack Delete inside the SearchBox — let it edit characters there.
                if (IsSearchBoxFocused()) return;
                ViewModel.DeleteSelectedCommand.Execute(null);
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

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires during XAML EndInit because of SelectedIndex="0", which is
        // before the ctor wires DataContext / ViewModel. Bail in that early phase — the
        // initial selection (All) already matches KindFilter's null default.
        if (ViewModel is null) return;
        if (FiltersBox.SelectedItem is not ComboBoxItem item) return;
        ViewModel.KindFilter = item.Tag switch
        {
            "Text"  => ItemKind.Text,
            "Image" => ItemKind.Image,
            _       => null,
        };
    }

    /// <summary>Hide on the next dispatcher cycle so the current event handler can fully
    /// unwind first. Hide() (not Close()) because the window is registered Singleton — the
    /// next Show reuses the same instance for an instant reopen.</summary>
    private void BeginHide()
    {
        if (_isClosing) return;
        _isClosing = true;
        Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Background);
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
        win.Show();
        win.Activate();
    }
}
