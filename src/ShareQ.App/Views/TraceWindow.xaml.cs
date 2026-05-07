using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ShareQ.AI;
using ShareQ.App.Services;
using ShareQ.App.ViewModels;

namespace ShareQ.App.Views;

/// <summary>Illustrator-style trace preview. Constructed with source PNG bytes; emits
/// <see cref="ResultSvg"/> on save. Modeless — host opens via <c>Show()</c> and awaits
/// <see cref="System.Windows.Window.Closed"/> through a <c>TaskCompletionSource</c>
/// (mirrors <c>ImageEffectsWindow</c>'s editor-mode plumbing). The preview pipeline
/// re-runs potrace on every parameter change with a 150ms debounce — turning the Preview
/// checkbox off pauses the auto-rerun and the user can drive it manually via the
/// <c>Trace</c> button (matches Illustrator's <c>Preview</c>/<c>Trace</c> pair).</summary>
public sealed partial class TraceWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IImageTracer _tracer;
    private readonly TracePresetStore _presetStore;
    private readonly byte[] _sourcePng;
    private readonly TraceParametersViewModel _params;
    private CancellationTokenSource? _previewCts;
    private string? _lastSvg;
    // True once the WebView2 has finished its initial NavigationCompleted — from that point
    // on we swap content via ExecuteScriptAsync instead of NavigateToString. The point is to
    // keep the document alive across re-traces so zoom/pan state survives parameter tweaks
    // (without this, every slider change snapped the user back to fit-to-window).
    private bool _previewDocumentReady;

    public string? ResultSvg { get; private set; }

    public TraceWindow(IImageTracer tracer, TracePresetStore presetStore, byte[] sourcePng)
    {
        _tracer = tracer;
        _presetStore = presetStore;
        _sourcePng = sourcePng;
        _params = new TraceParametersViewModel(presetStore);
        InitializeComponent();
        SourcePreview.Source = LoadBitmap(sourcePng);
        DataContext = _params;
        _params.PropertyChanged += OnParamsChanged;
        Loaded += async (_, _) =>
        {
            await SvgPreviewWeb.EnsureCoreWebView2Async();
            // Kill the browser-native Ctrl+wheel zoom so it doesn't compound with our
            // custom transform-based zoom. Without this both fire on the same event and
            // the user perceives "zoom keeps climbing past the cap" because the native
            // zoom factor is still climbing while our own z is clamped at 100.
            if (SvgPreviewWeb.CoreWebView2?.Settings is { } wv2Settings)
            {
                wv2Settings.IsZoomControlEnabled = false;
            }
            // Set _previewDocumentReady once the initial document has fully loaded so the
            // window.shareqSetContent function is defined before we try to call it. Each
            // subsequent NavigateToString (e.g. window resize edge cases) flips it back off
            // until the next NavigationCompleted fires.
            SvgPreviewWeb.NavigationCompleted += (_, _) => _previewDocumentReady = true;
            await _params.LoadCustomPresetsAsync(CancellationToken.None);
            // Restore the last-used preset so the user returns to whatever they were
            // dialling in. Falls back to [Default] when nothing is stored or the saved
            // name no longer exists (e.g. user deleted a custom preset between sessions).
            var lastName = await _presetStore.GetLastUsedAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(lastName))
            {
                var match = _params.Presets.FirstOrDefault(p => p.Name == lastName);
                if (match is not null) _params.SelectedPreset = match;
            }
            SchedulePreview();
        };
        Closed += async (_, _) =>
        {
            if (_params.SelectedPreset is { } p)
            {
                try { await _presetStore.SetLastUsedAsync(p.Name, CancellationToken.None); }
                catch { /* settings write is best-effort */ }
            }
        };
    }

    private void OnParamsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The View dropdown only changes how the same SVG is shown — no re-trace needed.
        if (e.PropertyName == nameof(TraceParametersViewModel.SelectedViewMode))
        {
            RefreshPreview();
            return;
        }
        // Sync the swatch fill when IgnoreColor changes from anywhere (preset apply, programmatic
        // reset). The picker click already updates the swatch directly, but presets that include
        // an IgnoreColor (Sketched Art, Silhouettes) need this to reflect the value visually.
        if (e.PropertyName == nameof(TraceParametersViewModel.IgnoreColor))
        {
            IgnoreSwatchFill.Fill = _params.IgnoreColor is { } col
                ? new System.Windows.Media.SolidColorBrush(col)
                : System.Windows.Media.Brushes.Transparent;
        }
        if (_params.PreviewEnabled) SchedulePreview();
    }

    private void SchedulePreview()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var snapshot = _params.ToOptions();
        _ = RunPreviewAsync(snapshot, cts.Token);
    }

    private async Task RunPreviewAsync(TraceOptions opts, CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct).ConfigureAwait(true);
            var svg = await _tracer.TraceAsync(_sourcePng, opts, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            _lastSvg = svg;
            _params.UpdateInfoFromSvg(svg);
            RefreshPreview();
        }
        catch (OperationCanceledException) { }
        catch { /* swallow — keeps the window responsive when a parameter combo throws */ }
    }

    /// <summary>Re-render the right pane based on the currently-selected View dropdown
    /// item. The 5 modes wrap the same trace SVG in different HTML templates: full result,
    /// stroked outlines on a checker bg, outlines + source overlay, source-only, etc.
    /// See <see cref="ViewModels.TraceViewModeRenderer"/> for the per-mode HTML builders.
    /// <para>First call: NavigateToString on the full HTML document. Subsequent calls:
    /// ExecuteScriptAsync into the existing document so we only swap the inner content,
    /// keeping zoom/pan state intact across re-traces — the user can stay zoomed in on a
    /// problem area while tweaking parameters and watch the fix land in place.</para></summary>
    private void RefreshPreview()
    {
        var mode = _params.SelectedViewMode?.Mode ?? TraceViewMode.TracingResult;
        if (_previewDocumentReady && SvgPreviewWeb.CoreWebView2 is { } cw)
        {
            var body = TraceViewModeRenderer.RenderBody(_lastSvg, _sourcePng, mode);
            // JsonSerializer.Serialize handles all the JS-string escaping (quotes, backslashes,
            // unicode) we'd otherwise have to roll by hand for an inline script literal.
            var bodyJs = System.Text.Json.JsonSerializer.Serialize(body);
            _ = cw.ExecuteScriptAsync($"window.shareqSetContent({bodyJs})");
            return;
        }
        var html = TraceViewModeRenderer.Render(_lastSvg, _sourcePng, mode);
        _previewDocumentReady = false;
        SvgPreviewWeb.NavigateToString(html);
    }

    private async void OnPresetSaveClicked(object sender, RoutedEventArgs e)
    {
        // Pre-fill the rename dialog with the current preset's name so the user can either
        // overwrite it (same name → store de-dupes by name) or quickly tweak it for a
        // variant ("3 Colors" → "3 Colors No BG"). Stock presets show their name too;
        // saving with a stock name effectively shadows it with a custom of the same name.
        var current = _params.SelectedPreset?.Name ?? string.Empty;
        var dlg = new TabTitleDialog("trace preset", current) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.TabTitle)) return;
        var name = dlg.TabTitle.Trim();
        var preset = new TracePreset(name, _params.ToOptions());
        await _presetStore.SaveAsync(preset, CancellationToken.None);
        await _params.LoadCustomPresetsAsync(CancellationToken.None);
        // Auto-select the just-saved preset. Without this, LoadCustomPresetsAsync re-picks
        // the previously-selected preset (e.g. "[Default]") and ApplyPreset wipes the
        // user's freshly-saved IgnoreColor / params back to that preset's defaults — exactly
        // the "creo un preset custom e me lo resetta" bug.
        var saved = _params.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (saved is not null) _params.SelectedPreset = saved;
    }

    private async void OnPresetDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_params.SelectedPreset is not { } p || !_params.SelectedPresetIsCustom) return;
        await _presetStore.DeleteAsync(p.Name, CancellationToken.None);
        await _params.LoadCustomPresetsAsync(CancellationToken.None);
    }

    private void OnTraceNowClicked(object sender, RoutedEventArgs e) => SchedulePreview();

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // Use the most recent preview SVG if Preview is on; otherwise force a sync trace.
        if (string.IsNullOrEmpty(_lastSvg))
        {
            _previewCts?.Cancel();
            _lastSvg = _tracer.TraceAsync(_sourcePng, _params.ToOptions(), CancellationToken.None).GetAwaiter().GetResult();
        }
        ResultSvg = _lastSvg;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>Click on the IgnoreColor swatch opens ShareQ's <see cref="ColorPickerWindow"/>
    /// (same picker used by image effects + theme + editor swatches). Includes the in-window
    /// eyedropper button via <see cref="ColorSwatchButton.EyedropperHandler"/> if a host
    /// has wired one up (the tray + editor wire it; the trace window inherits the wiring
    /// because it's process-wide static). On Apply, the picked colour writes back to the
    /// VM and the Rectangle fill updates so the user sees the chosen value in the swatch.</summary>
    private void OnIgnoreSwatchMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_params.IgnoreColorEnabled) return;
        e.Handled = true;
        var current = _params.IgnoreColor is { } c
            ? new ShareQ.Editor.Model.ShapeColor(c.A, c.R, c.G, c.B)
            : ShareQ.Editor.Model.ShapeColor.Black;
        var dlg = new ShareQ.Editor.Views.ColorPickerWindow(current) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ApplyIgnoreShapeColor(dlg.PickedColor);
    }

    private void ApplyIgnoreShapeColor(ShareQ.Editor.Model.ShapeColor sc)
    {
        var col = System.Windows.Media.Color.FromArgb(sc.A, sc.R, sc.G, sc.B);
        _params.IgnoreColor = col;
        _params.IgnoreColorEnabled = true;
        IgnoreSwatchFill.Fill = new System.Windows.Media.SolidColorBrush(col);
    }

    /// <summary>Eyedropper button next to the IgnoreColor swatch. Opens the full-screen
    /// <see cref="ScreenColorPickerService"/> overlay (same one the tray's color sampler /
    /// image-effects swatches use) so the user can sample any pixel on-screen — including
    /// pixels from the source preview to the left of the trace window.</summary>
    private void OnIgnoreEyedropperClicked(object sender, RoutedEventArgs e)
    {
        var picker = (Application.Current as App)?.Services?.GetService<ScreenColorPickerService>();
        if (picker is null) return;
        var hex = picker.SampleAtCursor();
        if (string.IsNullOrEmpty(hex)) return;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            ApplyIgnoreShapeColor(new ShareQ.Editor.Model.ShapeColor(c.A, c.R, c.G, c.B));
        }
        catch { /* malformed hex — ignore */ }
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
