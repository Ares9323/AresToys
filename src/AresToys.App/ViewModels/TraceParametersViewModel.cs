using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.AI;
using AresToys.App.Services;

namespace AresToys.App.ViewModels;

public enum TraceViewMode { TracingResult, TracingResultWithOutlines, Outlines, OutlinesWithSource, SourceImage }

public sealed record TraceViewModeItem(TraceViewMode Mode, string Label);
public sealed record TracePaletteItem(TracePalette Value, string Label);

/// <summary>VM for <see cref="Views.TraceWindow"/>. Holds every Illustrator parameter as
/// observable properties so the slider/combo bindings refresh automatically. Three layers
/// of reactivity:
/// <list type="number">
///   <item>Slider/combo edits → property change → window's <c>OnParamsChanged</c> kicks
///     a debounced re-trace.</item>
///   <item>Selecting a preset writes the preset's options into every property in one
///     shot via <see cref="ApplyPreset"/> — single change-batch (suspend notifications,
///     write all, resume) so the re-trace fires once instead of N times.</item>
///   <item>Mode change toggles palette enable + recomputes the parameter dock's column
///     visibility (mono shows Threshold; color/gray shows Colors + Palette).</item>
/// </list>
/// <see cref="UpdateInfoFromSvg"/> is invoked by the window after each preview render to
/// fill the Info readout (anchor / path / colour counts parsed straight from the SVG —
/// regex on <c>&lt;path/&gt;</c> + <c>fill=</c> attributes).</summary>
public sealed partial class TraceParametersViewModel : ObservableObject
{
    private readonly TracePresetStore _store;

    public TraceParametersViewModel(TracePresetStore store)
    {
        _store = store;
        ViewModes = new ObservableCollection<TraceViewModeItem>
        {
            new(TraceViewMode.TracingResult, "Tracing Result"),
            new(TraceViewMode.TracingResultWithOutlines, "Tracing Result with Outlines"),
            new(TraceViewMode.Outlines, "Outlines"),
            new(TraceViewMode.OutlinesWithSource, "Outlines with Source Image"),
            new(TraceViewMode.SourceImage, "Source Image"),
        };
        SelectedViewMode = ViewModes[0];
        Palettes = new ObservableCollection<TracePaletteItem>
        {
            new(TracePalette.Automatic, "Automatic"),
            new(TracePalette.Limited, "Limited"),
            new(TracePalette.FullTone, "Full Tone"),
            new(TracePalette.Custom, "Custom (pick colours)"),
        };
        SelectedPalette = Palettes[1];
        CustomPalette = new ObservableCollection<System.Windows.Media.Color>();
        CustomPalette.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsCustomPalette));
            OnPropertyChanged(nameof(CanAddSwatch));
            OnPropertyChanged(nameof(HasEnoughSwatches));
            OnPropertyChanged(nameof(ShowSwatchMinimumHint));
        };
        Presets = new ObservableCollection<TracePreset>(TracePresets.Stock);
        SelectedPreset = Presets[0];
    }

    // Preset list — stock + custom (loaded from store).
    public ObservableCollection<TracePreset> Presets { get; }
    [ObservableProperty] private TracePreset? _selectedPreset;
    public bool SelectedPresetIsCustom => SelectedPreset is { } p
        && !TracePresets.Stock.Any(s => string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedPresetChanged(TracePreset? value)
    {
        if (value is null) return;
        ApplyPreset(value.Options);
        OnPropertyChanged(nameof(SelectedPresetIsCustom));
    }

    public async Task LoadCustomPresetsAsync(CancellationToken ct)
    {
        var custom = await _store.GetAllAsync(ct).ConfigureAwait(true);
        // Rebuild list: stock first, then custom. Preserve current selection by name.
        var prevName = SelectedPreset?.Name;
        Presets.Clear();
        foreach (var s in TracePresets.Stock) Presets.Add(s);
        foreach (var c in custom) Presets.Add(c);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == prevName) ?? Presets[0];
    }

    // View mode + palette item lists.
    public ObservableCollection<TraceViewModeItem> ViewModes { get; }
    [ObservableProperty] private TraceViewModeItem _selectedViewMode = null!;
    public ObservableCollection<TracePaletteItem> Palettes { get; }
    [ObservableProperty] private TracePaletteItem _selectedPalette = null!;

    /// <summary>User-picked palette for <see cref="TracePalette.Custom"/> mode. Edited in the
    /// trace window via the swatch row (click → opens screen colour picker → fills slot).
    /// Each entry maps to a layer in the trace; pixels collapse to their nearest pick by
    /// Euclidean RGB so e.g. "white + light grey" become "white" if only white was picked.
    /// Capped at <see cref="MaxCustomSwatches"/> — the tracer handles many layers but UX
    /// breaks down past ~16 colour chips in a row.</summary>
    public ObservableCollection<System.Windows.Media.Color> CustomPalette { get; }
    public const int MaxCustomSwatches = 16;
    public const int MinCustomSwatches = 2;

    public bool IsCustomPalette => SelectedPalette?.Value == TracePalette.Custom;
    /// <summary>True while the user can still add another swatch (below the cap).</summary>
    public bool CanAddSwatch => CustomPalette.Count < MaxCustomSwatches;
    /// <summary>True once the user has picked at least <see cref="MinCustomSwatches"/>
    /// colours — below that the trace would map every pixel to one colour.</summary>
    public bool HasEnoughSwatches => CustomPalette.Count >= MinCustomSwatches;

    partial void OnSelectedPaletteChanged(TracePaletteItem value)
    {
        OnPropertyChanged(nameof(IsCustomPalette));
        OnPropertyChanged(nameof(IsColorDetailVisible));
        OnPropertyChanged(nameof(ShowSwatchRow));
    }

    /// <summary>The swatch row shows for the Custom palette of Color/Grayscale and always in
    /// Flat Logo mode, where the swatches are the logo colours.</summary>
    public bool ShowSwatchRow => Mode == TraceMode.Logo || (PaletteEnabled && IsCustomPalette);
    /// <summary>The "pick at least 2" hint never shows in Flat Logo (empty = automatic colours).</summary>
    public bool ShowSwatchMinimumHint => Mode != TraceMode.Logo && !HasEnoughSwatches;

    /// <summary>Color detail only applies to the Automatic and Full Tone palettes of Color or
    /// Grayscale mode.</summary>
    public bool IsColorDetailVisible => PaletteEnabled
        && SelectedPalette?.Value is TracePalette.Automatic or TracePalette.FullTone;

    // Mode toggle group — three RadioButton bindings funnel into one TraceMode field.
    [ObservableProperty] private TraceMode _mode = TraceMode.BlackAndWhite;
    public bool ModeColor { get => Mode == TraceMode.Color; set { if (value) Mode = TraceMode.Color; OnPropertyChanged(); } }
    public bool ModeGrayscale { get => Mode == TraceMode.Grayscale; set { if (value) Mode = TraceMode.Grayscale; OnPropertyChanged(); } }
    public bool ModeLogo { get => Mode == TraceMode.Logo; set { if (value) Mode = TraceMode.Logo; OnPropertyChanged(); } }
    public bool ModeBlackAndWhite { get => Mode == TraceMode.BlackAndWhite; set { if (value) Mode = TraceMode.BlackAndWhite; OnPropertyChanged(); } }
    partial void OnModeChanged(TraceMode value)
    {
        OnPropertyChanged(nameof(ModeColor));
        OnPropertyChanged(nameof(ModeGrayscale));
        OnPropertyChanged(nameof(ModeBlackAndWhite));
        OnPropertyChanged(nameof(ModeLogo));
        OnPropertyChanged(nameof(ShowSwatchRow));
        OnPropertyChanged(nameof(ShowSwatchMinimumHint));
        OnPropertyChanged(nameof(PaletteEnabled));
        OnPropertyChanged(nameof(IsBlackAndWhite));
        OnPropertyChanged(nameof(IsColorOrGrayscale));
        OnPropertyChanged(nameof(IsColorDetailVisible));
    }
    public bool PaletteEnabled => Mode is TraceMode.Color or TraceMode.Grayscale;
    public bool IsBlackAndWhite => Mode == TraceMode.BlackAndWhite;
    public bool IsColorOrGrayscale => !IsBlackAndWhite;

    // Slider-bound parameters.
    [ObservableProperty] private int _colorCount = 6;
    [ObservableProperty] private int _threshold = 128;
    [ObservableProperty] private int _pathsPercent = 50;
    [ObservableProperty] private int _cornersPercent = 75;
    [ObservableProperty] private int _noisePx = 25;
    [ObservableProperty] private TraceMethod _method = TraceMethod.Overlapping;
    [ObservableProperty] private bool _snapCurvesToLines = true;
    [ObservableProperty] private System.Windows.Media.Color? _ignoreColor;
    [ObservableProperty] private int _ignoreTolerance = 32;
    [ObservableProperty] private bool _autoGrouping = true;
    [ObservableProperty] private bool _previewEnabled = true;

    /// <summary>True when the Realtime Preview checkbox is OFF — the Manual Trace button
    /// is shown only in that case. With realtime preview on, every parameter change auto-
    /// re-traces via the 150 ms debounce, so the button would be a no-op visual clutter.</summary>
    public bool ManualTraceVisible => !PreviewEnabled;
    partial void OnPreviewEnabledChanged(bool value) => OnPropertyChanged(nameof(ManualTraceVisible));

    // Quality knobs — exposed because previously hardcoded constants were either too
    // weak (1-pass smoothing left visible saw-tooth on heavy AA sources) or too strong
    // (always-on box blur softened sharp logos that didn't need it). Defaults match the
    // previous hardcoded behaviour so existing presets render identically.
    [ObservableProperty] private int _preBlurStrength = 1;
    [ObservableProperty] private int _colorDetail = 50;
    [ObservableProperty] private bool _shapes;
    [ObservableProperty] private bool _gradients;
    [ObservableProperty] private bool _fills = true;
    [ObservableProperty] private bool _strokes;
    [ObservableProperty] private double _strokeWidth = 10;

    // Method radio bridges (Task 5 Step 2) — two RadioButtons funnel into the single
    // TraceMethod enum value, mirroring the Mode bridge pattern above.
    public bool MethodIsOverlapping { get => Method == TraceMethod.Overlapping; set { if (value) Method = TraceMethod.Overlapping; OnPropertyChanged(); } }
    public bool MethodIsAbutting { get => Method == TraceMethod.Abutting; set { if (value) Method = TraceMethod.Abutting; OnPropertyChanged(); } }
    partial void OnMethodChanged(TraceMethod value)
    {
        OnPropertyChanged(nameof(MethodIsOverlapping));
        OnPropertyChanged(nameof(MethodIsAbutting));
    }

    // Ignore-color enable toggle. When unchecked, also clears the picked colour so the next
    // ToOptions() call won't re-apply a stale ignore. IgnoreColor makes the matched pixels
    // transparent in the output.
    [ObservableProperty] private bool _ignoreColorEnabled;
    partial void OnIgnoreColorEnabledChanged(bool value)
    {
        if (!value) IgnoreColor = null;
    }

    // Info readout — refilled after each successful trace.
    [ObservableProperty] private string _infoAnchors = "Anchors: 0";
    [ObservableProperty] private string _infoPaths = "Paths: 0";
    [ObservableProperty] private string _infoColors = "Colors: 0";

    public TraceOptions ToOptions() => new(
        Mode: Mode,
        Palette: SelectedPalette?.Value ?? TracePalette.Limited,
        ColorCount: ColorCount,
        Threshold: Threshold,
        PathsPercent: PathsPercent,
        CornersPercent: CornersPercent,
        NoisePx: NoisePx,
        Method: Method,
        SnapCurvesToLines: SnapCurvesToLines,
        IgnoreColor: IgnoreColor is { } c ? System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B) : null,
        IgnoreTolerance: IgnoreTolerance,
        AutoGrouping: AutoGrouping,
        PreBlurStrength: PreBlurStrength,
        CustomPalette: CustomPalette.Count > 0
            ? CustomPalette.Select(m => System.Drawing.Color.FromArgb(m.A, m.R, m.G, m.B)).ToList()
            : null,
        ColorDetail: ColorDetail,
        Shapes: Shapes,
        Gradients: Gradients,
        Fills: Fills,
        Strokes: Strokes,
        StrokeWidth: StrokeWidth);

    /// <summary>Apply every field from <paramref name="o"/> in sequence. Each setter fires
    /// its own PropertyChanged → the window's <c>OnParamsChanged</c> queues N debounced
    /// re-traces, but only the LAST one wins (CTS cancels prior previews + 150ms wait
    /// coalesces the burst). No suspend / batching needed.</summary>
    public void ApplyPreset(TraceOptions o)
    {
        Mode = o.Mode;
        SelectedPalette = Palettes.FirstOrDefault(p => p.Value == o.Palette) ?? Palettes[1];
        ColorCount = o.ColorCount;
        Threshold = o.Threshold;
        PathsPercent = o.PathsPercent;
        CornersPercent = o.CornersPercent;
        NoisePx = o.NoisePx;
        Method = o.Method;
        SnapCurvesToLines = o.SnapCurvesToLines;
        IgnoreColor = o.IgnoreColor is { } c ? System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B) : null;
        // Sync the Ignore Color checkbox with the preset's IgnoreColor presence so the UI
        // state never drifts from the actual setting. Without this, switching between a
        // preset-with-IgnoreColor and one-without leaves the checkbox stale: it could be
        // checked while IgnoreColor is null (stock preset just loaded) or unchecked while
        // IgnoreColor was just restored from a custom preset, both confusing the user.
        IgnoreColorEnabled = o.IgnoreColor is not null;
        IgnoreTolerance = o.IgnoreTolerance;
        AutoGrouping = o.AutoGrouping;
        PreBlurStrength = o.PreBlurStrength;
        ColorDetail = o.ColorDetail;
        Shapes = o.Shapes;
        Gradients = o.Gradients;
        Fills = o.Fills;
        Strokes = o.Strokes;
        StrokeWidth = o.StrokeWidth;
        // Custom palette is only restored if the preset actually carries one. Stock presets
        // ship with null here, so loading one doesn't wipe the user's currently-picked
        // swatches (they'd be annoyed if switching to "High Fidelity Photo" cleared their
        // brand colours). Saved custom presets that DO carry a palette get full round-trip.
        if (o.CustomPalette is { Count: > 0 } picks)
        {
            CustomPalette.Clear();
            foreach (var pick in picks)
                CustomPalette.Add(System.Windows.Media.Color.FromArgb(pick.A, pick.R, pick.G, pick.B));
        }
    }

    /// <summary>Parse the rendered SVG to fill the Info readout. Cheap regex over the
    /// path / fill attributes; not a real XML parse because the SVG is stable enough
    /// (we built it ourselves in the multi-color path; the black and white path is also
    /// well-formed). Numbers are approximate — anchor count is the sum of "M"+"C"+"L"
    /// commands per path which over-counts curves slightly; matches what Illustrator
    /// shows close enough for a status readout.</summary>
    public void UpdateInfoFromSvg(string? svg)
    {
        if (string.IsNullOrEmpty(svg))
        {
            InfoAnchors = "Anchors: 0";
            InfoPaths = "Paths: 0";
            InfoColors = "Colors: 0";
            return;
        }
        var pathCount = System.Text.RegularExpressions.Regex.Count(svg, "<path");
        var anchorCount = System.Text.RegularExpressions.Regex.Count(svg, "[MCL]");
        var colourSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(svg, "fill=\"(#?[A-Fa-f0-9]{3,8}|[A-Za-z]+)\""))
        {
            colourSet.Add(m.Groups[1].Value);
        }
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        InfoAnchors = $"Anchors: {anchorCount.ToString(culture)}";
        InfoPaths = $"Paths: {pathCount.ToString(culture)}";
        InfoColors = $"Colors: {colourSet.Count.ToString(culture)}";
    }
}
