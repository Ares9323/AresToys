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
            await _params.LoadCustomPresetsAsync(CancellationToken.None);
            SchedulePreview();
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
    /// See <see cref="ViewModels.TraceViewModeRenderer"/> for the per-mode HTML builders.</summary>
    private void RefreshPreview()
    {
        var html = TraceViewModeRenderer.Render(
            _lastSvg, _sourcePng, _params.SelectedViewMode?.Mode ?? TraceViewMode.TracingResult);
        SvgPreviewWeb.NavigateToString(html);
    }

    private async void OnPresetSaveClicked(object sender, RoutedEventArgs e)
    {
        // TabTitleDialog signature: (tabKey, currentTitle); result lives on TabTitle (empty
        // string when the user cancels). Adapted from the plan's pseudocode (which used
        // Prompt/ResultText props that don't exist on this dialog).
        var dlg = new TabTitleDialog("trace preset", string.Empty) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.TabTitle)) return;
        var preset = new TracePreset(dlg.TabTitle.Trim(), _params.ToOptions());
        await _presetStore.SaveAsync(preset, CancellationToken.None);
        await _params.LoadCustomPresetsAsync(CancellationToken.None);
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

    /// <summary>Wires the "Pick…" button next to the Ignore Color swatch to the full-screen
    /// eyedropper (the same overlay the tray's Color Sampler uses). Falls back to a one-shot
    /// click on the source preview when <see cref="ScreenColorPickerService"/> isn't registered
    /// (always present in production DI; the fallback keeps the window usable in test harnesses
    /// or when a stripped App composition is in play).</summary>
    private void OnPickIgnoreClicked(object sender, RoutedEventArgs e)
    {
        // Plan called for picker.PickAsync(Action<Color>) but the real service exposes a
        // synchronous string? SampleAtCursor() that returns a "#RRGGBB" hex (or null on
        // cancel) and internally pumps the overlay via Dispatcher.Invoke. Adapted accordingly.
        var picker = (Application.Current as App)?.Services?.GetService<ScreenColorPickerService>();
        if (picker is null) { PickFromSourceInline(); return; }
        var hex = picker.SampleAtCursor();
        if (hex is null) return; // user cancelled
        if (!TryParseHexColor(hex, out var col)) return;
        ApplyIgnoreColor(col);
    }

    /// <summary>Fallback when the screen-picker service is missing: arms a one-shot mouse hook
    /// on the left-pane source preview and reads the clicked pixel straight out of the
    /// <see cref="BitmapSource"/>.</summary>
    private void PickFromSourceInline()
    {
        void OneShot(object s, System.Windows.Input.MouseButtonEventArgs me)
        {
            SourcePreview.MouseLeftButtonDown -= OneShot;
            if (SourcePreview.Source is not BitmapSource src) return;
            var rel = me.GetPosition(SourcePreview);
            if (SourcePreview.ActualWidth <= 0 || SourcePreview.ActualHeight <= 0) return;
            var px = (int)(rel.X / SourcePreview.ActualWidth * src.PixelWidth);
            var py = (int)(rel.Y / SourcePreview.ActualHeight * src.PixelHeight);
            if (px < 0 || py < 0 || px >= src.PixelWidth || py >= src.PixelHeight) return;
            var pixel = new byte[4];
            src.CopyPixels(new System.Windows.Int32Rect(px, py, 1, 1), pixel, 4, 0);
            // BitmapSource default for PNG-loaded BitmapImage is Bgra32 — pixel layout B,G,R,A.
            var col = System.Windows.Media.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            ApplyIgnoreColor(col);
        }
        SourcePreview.MouseLeftButtonDown += OneShot;
    }

    private void ApplyIgnoreColor(System.Windows.Media.Color col)
    {
        _params.IgnoreColor = col;
        _params.IgnoreColorEnabled = true;
        IgnoreSwatch.Background = new System.Windows.Media.SolidColorBrush(col);
    }

    private static bool TryParseHexColor(string hex, out System.Windows.Media.Color color)
    {
        try
        {
            color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
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
