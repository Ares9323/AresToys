using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.ImageEffects;
using AresToys.ImageEffects.Drawing;
using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.App.ViewModels.ImageEffects;

/// <summary>One row in the property grid: a single tunable property exposed by an
/// <see cref="ImageEffect"/>. The view-model is discriminated by <see cref="Kind"/>: the
/// XAML template branches on <c>IsFloat / IsBool / IsColor / IsPadding / IsEnum</c> to pick
/// the right control (slider / checkbox / colour swatch / 4-up grid / combo). Float and int
/// effects share the same slider+textbox path through <see cref="Value"/>; the other types
/// store their state in dedicated properties so we don't have to box through object.</summary>
public sealed partial class EffectParameterViewModel : ObservableObject
{
    private readonly ImageEffect _effect;
    private readonly PropertyInfo _property;
    private bool _suppress;

    public string Label { get; set; }
    /// <summary>Slider lower bound. <c>[ObservableProperty]</c> so a parent VM can retune the
    /// range at runtime — e.g. DrawImage flips Width/Height between absolute pixels (0..4000)
    /// and percentages (0..200) based on SizeMode, and the Slider's <c>Minimum</c> / <c>Maximum</c>
    /// bindings refresh in place.</summary>
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max;
    [ObservableProperty] private double _step;
    public string ValueFormat { get; set; }

    /// <summary>External gate flipped by sibling parameters (e.g. DrawImage's SizeMode hides
    /// Width/Height when set to DontResize). Folds into <see cref="IsActive"/> so the entire
    /// row collapses out of the property grid rather than greying out — disabled-but-present
    /// controls add noise without giving the user something to do.</summary>
    [ObservableProperty] private bool _isApplicable = true;

    /// <summary>The CLR property name on the underlying <see cref="ImageEffect"/>. Exposed so
    /// the entry view-model can pair Color/UseGradient/Gradient triplets by naming convention
    /// (e.g. <c>OutlineColor</c>+<c>OutlineUseGradient</c>+<c>OutlineGradient</c>).</summary>
    public string PropertyName => _property.Name;

    public EffectParameterKind Kind { get; }
    public bool IsNumber => Kind == EffectParameterKind.Number;
    public bool IsBool => Kind == EffectParameterKind.Bool;
    public bool IsColor => Kind == EffectParameterKind.Color;
    public bool IsPadding => Kind == EffectParameterKind.Padding;
    public bool IsEnum => Kind == EffectParameterKind.Enum;
    public bool IsString => Kind == EffectParameterKind.Text;
    public bool IsGradient => Kind == EffectParameterKind.Gradient;
    public bool IsFont => Kind == EffectParameterKind.Font;
    public bool IsFilePath => Kind == EffectParameterKind.FilePath;

    /// <summary>Optional paired UseGradient toggle for Color/Gradient parameters that share
    /// a triplet. The paired Color row keeps its slot in the property grid and swaps its
    /// inner swatch/button between Color and Gradient based on the toggle; the standalone
    /// Gradient row hides entirely (its data is rendered through the Color row instead).</summary>
    public EffectParameterViewModel? PairedToggle
    {
        get => _pairedToggle;
        set
        {
            if (ReferenceEquals(_pairedToggle, value)) return;
            if (_pairedToggle is not null) _pairedToggle.PropertyChanged -= OnPairedTogglePropertyChanged;
            _pairedToggle = value;
            if (_pairedToggle is not null) _pairedToggle.PropertyChanged += OnPairedTogglePropertyChanged;
            RaisePairedFlagsChanged();
        }
    }
    private EffectParameterViewModel? _pairedToggle;

    /// <summary>The Gradient parameter that lives in the same triplet as this Color row.
    /// When set, the Color row's template binds its Gradient swatch through this reference
    /// so the toggle can flip the displayed content in place.</summary>
    public EffectParameterViewModel? PairedGradient
    {
        get => _pairedGradient;
        set
        {
            if (ReferenceEquals(_pairedGradient, value)) return;
            if (_pairedGradient is not null) _pairedGradient.PropertyChanged -= OnPairedGradientPropertyChanged;
            _pairedGradient = value;
            if (_pairedGradient is not null) _pairedGradient.PropertyChanged += OnPairedGradientPropertyChanged;
            OnPropertyChanged(nameof(PairedGradient));
            RaisePairedFlagsChanged();
        }
    }
    private EffectParameterViewModel? _pairedGradient;

    private void OnPairedTogglePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoolValue)) RaisePairedFlagsChanged();
    }

    private void OnPairedGradientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward GradientBrush updates so the Color-row swatch refreshes after the editor
        // dialog writes back into the paired Gradient parameter.
        if (e.PropertyName == nameof(GradientBrush)) OnPropertyChanged(nameof(PairedGradient));
    }

    private void RaisePairedFlagsChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ShowsColor));
        OnPropertyChanged(nameof(ShowsPairedGradient));
    }

    /// <summary>Set by <see cref="SideToggleGroupViewModel.TryCreate"/> when this parameter
    /// is one of Top/Right/Bottom/Left/Curved — the compass cluster in the property panel
    /// owns the rendering, so the linear list must skip the row.</summary>
    public bool IsInSideGroup { get; internal set; }

    /// <summary>Marker for the UseGradient bool that pairs a Color/Gradient triplet. The
    /// Color row renders this toggle as an inline checkbox next to the swatch, so the
    /// linear list must skip the standalone row.</summary>
    public bool IsPairedToggle { get; internal set; }

    /// <summary>True when this parameter row should be visible in the linear param list.
    /// Side-group members hide (the compass renders them); the paired UseGradient hides
    /// (the Color row renders it inline); a paired Gradient hides (the Color row swaps in
    /// place); everything else stays.</summary>
    public bool IsActive
    {
        get
        {
            if (!IsApplicable) return false;
            if (IsInSideGroup) return false;
            if (IsPairedToggle) return false;
            return Kind switch
            {
                EffectParameterKind.Gradient => _pairedToggle is null,
                _ => true,
            };
        }
    }

    partial void OnIsApplicableChanged(bool value) => OnPropertyChanged(nameof(IsActive));

    /// <summary>For a Color row: show the Color swatch + Pick… button. True for unpaired
    /// Color params, or paired Color params whose toggle is off.</summary>
    public bool ShowsColor => IsColor && (_pairedToggle is null || !_pairedToggle.BoolValue);

    /// <summary>For a paired Color row: show the linked Gradient's swatch + Edit… button
    /// instead of the Color one. True only when toggled on and a paired Gradient exists.</summary>
    public bool ShowsPairedGradient => IsColor
        && _pairedToggle is not null && _pairedToggle.BoolValue
        && _pairedGradient is not null;

    public Action? Changed { get; set; }

    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private SKColor _colorValue;
    [ObservableProperty] private int _paddingLeft;
    [ObservableProperty] private int _paddingTop;
    [ObservableProperty] private int _paddingRight;
    [ObservableProperty] private int _paddingBottom;
    [ObservableProperty] private string? _enumValue;
    [ObservableProperty] private string _stringValue = string.Empty;
    [ObservableProperty] private GradientInfo? _gradientValue;

    /// <summary>Transient UI flag for Padding rows: when true, editing any of L/T/R/B mirrors
    /// the new value into the other three. Initial value is whatever current state is uniform
    /// (all four equal); not persisted because it's purely an editing convenience.</summary>
    [ObservableProperty] private bool _isUniform;
    private bool _suppressUniform;

    // Font fields — split presentation of the underlying ShareX font descriptor string. The
    // round-trip path is: incoming string is parsed into these four fields by the constructor;
    // any change to a field reformats the descriptor and pushes it through StringValue.
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private double _fontSizePoints = 36;
    [ObservableProperty] private bool _fontBold;
    [ObservableProperty] private bool _fontItalic;

    /// <summary>Installed font families (lazy + cached for the process). Bound to the popup
    /// list in the Font row so the user can search through everything Windows exposes.</summary>
    public static IReadOnlyList<string> SystemFontFamilies => _systemFontFamilies.Value;
    private static readonly Lazy<IReadOnlyList<string>> _systemFontFamilies = new(() =>
        System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>Frozen <see cref="SolidColorBrush"/> form of <see cref="ColorValue"/> — bound
    /// to the swatch <c>Background</c> in XAML. Recomputed on every colour change so the swatch
    /// stays in sync after the picker dialog writes back. We deliberately use System.Windows.Media
    /// types here (rather than a string + converter) to keep the data path single-hop.</summary>
    public Brush ColorBrush => BuildBrush(ColorValue);

    /// <summary>WPF brush for the gradient swatch in the property grid — a tiny preview
    /// thumbnail that lets the user see the chosen gradient without opening the editor.</summary>
    public Brush GradientBrush => GradientValue is null ? Brushes.Transparent : ToWpfBrush(GradientValue);

    private static Brush BuildBrush(SKColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        brush.Freeze();
        return brush;
    }

    /// <summary>Convert our domain <see cref="GradientInfo"/> into a frozen
    /// <see cref="LinearGradientBrush"/>. Direction enum maps to the four diagonal/cardinal
    /// vectors WPF expects in unit-square coordinates.</summary>
    public static LinearGradientBrush ToWpfBrush(GradientInfo info)
    {
        var brush = new LinearGradientBrush();
        switch (info.Type)
        {
            case LinearGradientMode.Horizontal: brush.StartPoint = new System.Windows.Point(0, 0); brush.EndPoint = new System.Windows.Point(1, 0); break;
            case LinearGradientMode.Vertical: brush.StartPoint = new System.Windows.Point(0, 0); brush.EndPoint = new System.Windows.Point(0, 1); break;
            case LinearGradientMode.ForwardDiagonal: brush.StartPoint = new System.Windows.Point(0, 0); brush.EndPoint = new System.Windows.Point(1, 1); break;
            case LinearGradientMode.BackwardDiagonal: brush.StartPoint = new System.Windows.Point(1, 0); brush.EndPoint = new System.Windows.Point(0, 1); break;
        }
        foreach (var stop in info.Colors)
        {
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                Color.FromArgb(stop.Color.Alpha, stop.Color.Red, stop.Color.Green, stop.Color.Blue),
                Math.Clamp(stop.Location / 100.0, 0, 1)));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>Pair of (raw enum name, localised display name) so the ComboBox can show
    /// translations while the bound SelectedValue stays the English enum name (round-trips
    /// through .sxie / store unchanged).</summary>
    public sealed record EnumOption(string Raw, string Display);

    public ObservableCollection<EnumOption> EnumOptions { get; } = new();

    public EffectParameterViewModel(ImageEffect effect, PropertyInfo property)
    {
        _effect = effect;
        _property = property;
        var attr = property.GetCustomAttribute<EffectParameterAttribute>();
        // Localise via the property's CLR name as a global key (Param_Amount / Param_Strength /
        // …). Falls through to the attribute DisplayName, then a Humanize() pass on the raw
        // property name — same fallback chain as before, just with a translation hop in front.
        var fallback = attr?.DisplayName ?? Humanize(property.Name);
        Label = Services.ImageEffectLocalizer.LocalizeParameter(property.Name, fallback);
        _min = attr?.Min ?? -100;
        _max = attr?.Max ?? 100;
        _step = attr?.Step ?? 1;
        ValueFormat = (attr?.Decimals ?? 0) > 0 ? $"F{attr!.Decimals}" : "F0";

        Kind = ResolveKind(property.PropertyType);

        _suppress = true;
        var current = property.GetValue(effect);
        switch (Kind)
        {
            case EffectParameterKind.Number:
                _value = current is null ? 0 : Convert.ToDouble(current, CultureInfo.InvariantCulture);
                break;
            case EffectParameterKind.Bool:
                _boolValue = (bool?)current ?? false;
                break;
            case EffectParameterKind.Color:
                _colorValue = current is SKColor c ? c : SKColors.Transparent;
                OnPropertyChanged(nameof(ColorBrush));
                break;
            case EffectParameterKind.Padding:
                if (current is Padding p)
                {
                    _paddingLeft = p.Left; _paddingTop = p.Top; _paddingRight = p.Right; _paddingBottom = p.Bottom;
                    // Pre-tick the Uniform chip when the four sides start out equal — a small UX
                    // hint that the user is editing what already looks like a uniform padding.
                    _isUniform = p.Left == p.Top && p.Top == p.Right && p.Right == p.Bottom;
                }
                break;
            case EffectParameterKind.Enum:
                foreach (var name in Enum.GetNames(property.PropertyType))
                    EnumOptions.Add(new EnumOption(
                        name,
                        Services.ImageEffectLocalizer.LocalizeEnumValue(name, Humanize(name))));
                _enumValue = current?.ToString() ?? EnumOptions.FirstOrDefault()?.Raw;
                break;
            case EffectParameterKind.Text:
            case EffectParameterKind.FilePath:
                _stringValue = current as string ?? string.Empty;
                break;
            case EffectParameterKind.Font:
                _stringValue = current as string ?? string.Empty;
                var parsed = ParseFontDescriptor(_stringValue);
                _fontFamily = parsed.Family;
                _fontSizePoints = parsed.Size;
                _fontBold = parsed.Bold;
                _fontItalic = parsed.Italic;
                break;
            case EffectParameterKind.Gradient:
                _gradientValue = current as GradientInfo ?? new GradientInfo();
                OnPropertyChanged(nameof(GradientBrush));
                break;
        }
        _suppress = false;
    }

    /// <summary>Convert a CamelCase property name into a human-readable label —
    /// "UseGradient" → "Use gradient", "OutlineUseGradient" → "Outline use gradient".
    /// Used as the fallback when no <see cref="EffectParameterAttribute.DisplayName"/> is set.</summary>
    private static string Humanize(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return propertyName;
        var sb = new StringBuilder(propertyName.Length + 4);
        sb.Append(char.ToUpperInvariant(propertyName[0]));
        for (var i = 1; i < propertyName.Length; i++)
        {
            var ch = propertyName[i];
            if (char.IsUpper(ch))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private EffectParameterKind ResolveKind(Type t)
    {
        if (t == typeof(float) || t == typeof(double) || t == typeof(int)) return EffectParameterKind.Number;
        if (t == typeof(bool)) return EffectParameterKind.Bool;
        if (t == typeof(SKColor)) return EffectParameterKind.Color;
        if (t == typeof(Padding)) return EffectParameterKind.Padding;
        if (t.IsEnum) return EffectParameterKind.Enum;
        if (t == typeof(GradientInfo)) return EffectParameterKind.Gradient;
        if (t == typeof(string))
        {
            // Heuristic by property name: "Font" is the ShareX font descriptor (family +
            // size + style), "ImageLocation" is a filesystem path that gets a browse button.
            // Other strings (Text content, etc.) stay a plain Text row.
            return _property.Name switch
            {
                "Font" => EffectParameterKind.Font,
                "ImageLocation" => EffectParameterKind.FilePath,
                _ => EffectParameterKind.Text,
            };
        }
        return EffectParameterKind.Unknown;
    }

    partial void OnValueChanged(double value)
    {
        if (_suppress || Kind != EffectParameterKind.Number) return;
        // Cast back to the property's CLR type. Effects use float almost universally; ints and
        // doubles are tolerated through Convert.ChangeType so an integer parameter still
        // round-trips cleanly through the double-typed Slider.
        var typed = Convert.ChangeType(value, _property.PropertyType, CultureInfo.InvariantCulture);
        _property.SetValue(_effect, typed);
        Changed?.Invoke();
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (_suppress || Kind != EffectParameterKind.Bool) return;
        _property.SetValue(_effect, value);
        Changed?.Invoke();
    }

    partial void OnColorValueChanged(SKColor value)
    {
        if (_suppress || Kind != EffectParameterKind.Color) return;
        _property.SetValue(_effect, value);
        OnPropertyChanged(nameof(ColorBrush));
        Changed?.Invoke();
    }

    private void PushPadding()
    {
        if (_suppress || Kind != EffectParameterKind.Padding) return;
        _property.SetValue(_effect, new Padding(PaddingLeft, PaddingTop, PaddingRight, PaddingBottom));
        Changed?.Invoke();
    }

    /// <summary>When the Uniform chip is on, mirror the just-edited side into the other three.
    /// <see cref="_suppressUniform"/> guards re-entrancy: setting the siblings re-fires their
    /// own OnXxxChanged partials, which would otherwise loop. The check on _suppress means
    /// the constructor's initial assignment doesn't trigger propagation either.</summary>
    private void PropagateUniform(PaddingSide skip, int value)
    {
        if (_suppress || _suppressUniform || !IsUniform) return;
        _suppressUniform = true;
        try
        {
            if (skip != PaddingSide.Left) PaddingLeft = value;
            if (skip != PaddingSide.Top) PaddingTop = value;
            if (skip != PaddingSide.Right) PaddingRight = value;
            if (skip != PaddingSide.Bottom) PaddingBottom = value;
        }
        finally { _suppressUniform = false; }
    }

    partial void OnPaddingLeftChanged(int value) { PushPadding(); PropagateUniform(PaddingSide.Left, value); }
    partial void OnPaddingTopChanged(int value) { PushPadding(); PropagateUniform(PaddingSide.Top, value); }
    partial void OnPaddingRightChanged(int value) { PushPadding(); PropagateUniform(PaddingSide.Right, value); }
    partial void OnPaddingBottomChanged(int value) { PushPadding(); PropagateUniform(PaddingSide.Bottom, value); }

    private enum PaddingSide { Left, Top, Right, Bottom }

    partial void OnEnumValueChanged(string? value)
    {
        if (_suppress || Kind != EffectParameterKind.Enum || value is null) return;
        if (Enum.TryParse(_property.PropertyType, value, out var typed))
        {
            _property.SetValue(_effect, typed);
            Changed?.Invoke();
        }
    }

    partial void OnStringValueChanged(string value)
    {
        if (_suppress) return;
        if (Kind != EffectParameterKind.Text && Kind != EffectParameterKind.Font && Kind != EffectParameterKind.FilePath) return;
        _property.SetValue(_effect, value);
        Changed?.Invoke();
    }

    // Font field changes — reformat the descriptor and push it through StringValue, which in
    // turn writes the underlying string property on the effect. The _suppressFont guard keeps
    // the constructor's per-field initial assignments from triggering a write.
    partial void OnFontFamilyChanged(string value) => UpdateFontDescriptor();
    partial void OnFontSizePointsChanged(double value) => UpdateFontDescriptor();
    partial void OnFontBoldChanged(bool value) => UpdateFontDescriptor();
    partial void OnFontItalicChanged(bool value) => UpdateFontDescriptor();

    private void UpdateFontDescriptor()
    {
        if (_suppress || Kind != EffectParameterKind.Font) return;
        StringValue = FormatFontDescriptor(FontFamily, FontSizePoints, FontBold, FontItalic);
    }

    /// <summary>Parse a ShareX font descriptor (<c>"Times New Roman, 36pt, style=Bold"</c>)
    /// into its four components. Size is kept in points (no pt→px conversion here — that
    /// happens at apply-time in the effect itself). Tolerant of missing fields, defaults to
    /// Arial/36pt/Regular.</summary>
    private static (string Family, double Size, bool Bold, bool Italic) ParseFontDescriptor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("Arial", 36, false, false);
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var family = parts.Length > 0 ? parts[0] : "Arial";
        double size = 36;
        if (parts.Length > 1)
        {
            var sizeStr = new string(parts[1].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            if (double.TryParse(sizeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                size = parsed;
        }
        bool bold = false, italic = false;
        for (var i = 2; i < parts.Length; i++)
        {
            var s = parts[i].ToLowerInvariant();
            if (s.Contains("bold")) bold = true;
            if (s.Contains("italic")) italic = true;
        }
        return (family, size, bold, italic);
    }

    private static string FormatFontDescriptor(string family, double sizePoints, bool bold, bool italic)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(family) ? "Arial" : family);
        sb.Append(", ");
        sb.Append(sizePoints.ToString("F0", CultureInfo.InvariantCulture));
        sb.Append("pt");
        if (bold && italic) sb.Append(", style=Bold|Italic");
        else if (bold) sb.Append(", style=Bold");
        else if (italic) sb.Append(", style=Italic");
        return sb.ToString();
    }

    /// <summary>Called by <see cref="ImageEffectsWindow"/> after the user closes the gradient
    /// editor with OK. Replaces the in-place GradientInfo on the underlying effect (we keep
    /// the same reference rather than replacing) and refreshes the preview swatch.</summary>
    public void NotifyGradientChanged()
    {
        if (Kind != EffectParameterKind.Gradient) return;
        OnPropertyChanged(nameof(GradientValue));
        OnPropertyChanged(nameof(GradientBrush));
        Changed?.Invoke();
    }
}

public enum EffectParameterKind
{
    Unknown,
    /// <summary>Float / double / int — rendered as slider + numeric textbox.</summary>
    Number,
    Bool,
    Color,
    Padding,
    Enum,
    /// <summary>Free-form string — rendered as a TextBox. Used for watermark text, image
    /// paths, etc. Named "Text" instead of "String" so the analyzer doesn't flag the enum
    /// value as colliding with the CLR type name.</summary>
    Text,
    /// <summary>Linear gradient — rendered as a clickable swatch that opens
    /// <see cref="AresToys.App.Views.GradientEditorWindow"/>.</summary>
    Gradient,
    /// <summary>ShareX font descriptor (<c>"Family, Size[pt][, style=Bold|Italic]"</c>) —
    /// rendered as a searchable family picker + separate size slider + Bold/Italic chips.
    /// Stored as a string on the underlying effect; the VM round-trips through
    /// <see cref="EffectParameterViewModel.FontFamily"/> / <c>FontSizePoints</c> /
    /// <c>FontBold</c> / <c>FontItalic</c>.</summary>
    Font,
    /// <summary>Filesystem path string — rendered as a TextBox + browse button. Detected
    /// by property name (currently <c>ImageLocation</c> on DrawImage). Treated as Text under
    /// the hood, just with the picker affordance.</summary>
    FilePath,
}
