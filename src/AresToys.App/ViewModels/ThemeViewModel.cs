using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services;

namespace AresToys.App.ViewModels;

/// <summary>Two-way wrapper around <see cref="ThemeService"/> for the Theme settings tab. Holds
/// the colors as hex strings (so the UI can use a plain TextBox + a swatch preview) and validates
/// before pushing the value back to the service. Reset goes through the service so persistence
/// stays a single owner.</summary>
public sealed partial class ThemeViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private bool _suppressApply;

    public ThemeViewModel(ThemeService theme)
    {
        _theme = theme;
        SyncFromService();
        // The service raises Changed after Reset / external change — re-pull so the inputs and
        // swatches stay in sync with the actual applied colors.
        _theme.Changed += (_, _) => SyncFromService();
    }

    /// <summary>Curated presets the user can apply from the dropdown. The first entry is the
    /// "Custom (current values)" sentinel — selecting it is a no-op so opening the tab doesn't
    /// clobber the user's colours.</summary>
    public IReadOnlyList<ThemePreset> Presets => ThemePresets.All;

    [ObservableProperty]
    private ThemePreset _selectedPreset = ThemePresets.Custom;

    partial void OnSelectedPresetChanged(ThemePreset value)
    {
        // Sentinel "Custom" → leave the user's hex values alone; the dropdown can re-snap to it
        // any time without side effects. Real preset → push every field at once with apply
        // suppressed, then a single TryApply at the end persists + paints in one go.
        if (value is null || ReferenceEquals(value, ThemePresets.Custom)) return;
        _suppressApply = true;
        AccentBackgroundLightHex = value.AccentBackgroundLightHex;
        AccentForegroundLightHex = value.AccentForegroundLightHex;
        AccentBackgroundDarkHex = value.AccentBackgroundDarkHex;
        AccentForegroundDarkHex = value.AccentForegroundDarkHex;
        AccentDangerHex = value.AccentDangerHex;
        Surface1Hex = value.Surface1Hex;
        Surface2Hex = value.Surface2Hex;
        Surface3Hex = value.Surface3Hex;
        OuterBorderHex = value.OuterBorderHex;
        InnerBorderHex = value.InnerBorderHex;
        _suppressApply = false;
        TryApply();
    }

    [ObservableProperty]
    private string _AccentBackgroundLightHex = "#751C8B";

    [ObservableProperty]
    private string _AccentForegroundLightHex = "#FFFFFF";

    [ObservableProperty]
    private string _accentBackgroundDarkHex = "#371242";

    [ObservableProperty]
    private string _accentForegroundDarkHex = "#878787";

    [ObservableProperty]
    private string _AccentDangerHex = "#8F2720";

    [ObservableProperty]
    private string _surface1Hex = "#1A1A1A";

    [ObservableProperty]
    private string _surface2Hex = "#1F1F1F";

    [ObservableProperty]
    private string _surface3Hex = "#2D2D2D";

    [ObservableProperty]
    private string _outerBorderHex = "#4A4A4A";

    [ObservableProperty]
    private string _innerBorderHex = "#2D2D2D";

    /// <summary>Live preview brushes for the swatch rectangles. Updated whenever the hex strings
    /// parse cleanly; left untouched on parse failure so the user can keep typing.</summary>
    [ObservableProperty]
    private Brush _accentBackgroundPreview = new SolidColorBrush(ThemeService.DefaultBackground);

    [ObservableProperty]
    private Brush _accentForegroundPreview = new SolidColorBrush(ThemeService.DefaultForeground);

    [ObservableProperty]
    private Brush _accentBackgroundDarkPreview = new SolidColorBrush(ThemeService.DefaultAccentDark);

    [ObservableProperty]
    private Brush _accentForegroundDarkPreview = new SolidColorBrush(ThemeService.DefaultAccentForegroundDark);

    [ObservableProperty]
    private Brush _accentDeletePreview = new SolidColorBrush(ThemeService.DefaultAccentDelete);

    [ObservableProperty]
    private Brush _surface1Preview = new SolidColorBrush(ThemeService.DefaultSurface1);

    [ObservableProperty]
    private Brush _surface2Preview = new SolidColorBrush(ThemeService.DefaultSurface2);

    [ObservableProperty]
    private Brush _surface3Preview = new SolidColorBrush(ThemeService.DefaultSurface3);

    [ObservableProperty]
    private Brush _outerBorderPreview = new SolidColorBrush(ThemeService.DefaultOuterBorder);

    [ObservableProperty]
    private Brush _innerBorderPreview = new SolidColorBrush(ThemeService.DefaultInnerBorder);

    partial void OnAccentBackgroundLightHexChanged(string value) => TryApply();
    partial void OnAccentForegroundLightHexChanged(string value) => TryApply();
    partial void OnAccentBackgroundDarkHexChanged(string value) => TryApply();
    partial void OnAccentForegroundDarkHexChanged(string value) => TryApply();
    partial void OnAccentDangerHexChanged(string value) => TryApply();
    partial void OnSurface1HexChanged(string value) => TryApply();
    partial void OnSurface2HexChanged(string value) => TryApply();
    partial void OnSurface3HexChanged(string value) => TryApply();
    partial void OnOuterBorderHexChanged(string value) => TryApply();
    partial void OnInnerBorderHexChanged(string value) => TryApply();

    [RelayCommand]
    private async Task ResetAsync() => await _theme.ResetAsync().ConfigureAwait(true);

    private void SyncFromService()
    {
        _suppressApply = true;
        AccentBackgroundLightHex = ThemeService.ToHex(_theme.AccentBackground);
        AccentForegroundLightHex = ThemeService.ToHex(_theme.AccentForeground);
        AccentBackgroundDarkHex = ThemeService.ToHex(_theme.AccentBackgroundDark);
        AccentForegroundDarkHex = ThemeService.ToHex(_theme.AccentForegroundDark);
        AccentDangerHex = ThemeService.ToHex(_theme.AccentDelete);
        Surface1Hex = ThemeService.ToHex(_theme.Surface1);
        Surface2Hex = ThemeService.ToHex(_theme.Surface2);
        Surface3Hex = ThemeService.ToHex(_theme.Surface3);
        OuterBorderHex = ThemeService.ToHex(_theme.OuterBorder);
        InnerBorderHex = ThemeService.ToHex(_theme.InnerBorder);
        AccentBackgroundPreview = Freeze(new SolidColorBrush(_theme.AccentBackground));
        AccentForegroundPreview = Freeze(new SolidColorBrush(_theme.AccentForeground));
        AccentBackgroundDarkPreview = Freeze(new SolidColorBrush(_theme.AccentBackgroundDark));
        AccentForegroundDarkPreview = Freeze(new SolidColorBrush(_theme.AccentForegroundDark));
        AccentDeletePreview = Freeze(new SolidColorBrush(_theme.AccentDelete));
        Surface1Preview = Freeze(new SolidColorBrush(_theme.Surface1));
        Surface2Preview = Freeze(new SolidColorBrush(_theme.Surface2));
        Surface3Preview = Freeze(new SolidColorBrush(_theme.Surface3));
        OuterBorderPreview = Freeze(new SolidColorBrush(_theme.OuterBorder));
        InnerBorderPreview = Freeze(new SolidColorBrush(_theme.InnerBorder));
        _suppressApply = false;
    }

    private void TryApply()
    {
        if (_suppressApply) return;
        var bg = ParseOrNull(AccentBackgroundLightHex);
        var fg = ParseOrNull(AccentForegroundLightHex);
        var dark = ParseOrNull(AccentBackgroundDarkHex);
        var fgDark = ParseOrNull(AccentForegroundDarkHex);
        var del = ParseOrNull(AccentDangerHex);
        var s1 = ParseOrNull(Surface1Hex);
        var s2 = ParseOrNull(Surface2Hex);
        var s3 = ParseOrNull(Surface3Hex);
        var outerBorder = ParseOrNull(OuterBorderHex);
        var innerBorder = ParseOrNull(InnerBorderHex);
        if (bg is null || fg is null || dark is null || fgDark is null || del is null
            || s1 is null || s2 is null || s3 is null || outerBorder is null || innerBorder is null) return;

        AccentBackgroundPreview = Freeze(new SolidColorBrush(bg.Value));
        AccentForegroundPreview = Freeze(new SolidColorBrush(fg.Value));
        AccentBackgroundDarkPreview = Freeze(new SolidColorBrush(dark.Value));
        AccentForegroundDarkPreview = Freeze(new SolidColorBrush(fgDark.Value));
        AccentDeletePreview = Freeze(new SolidColorBrush(del.Value));
        Surface1Preview = Freeze(new SolidColorBrush(s1.Value));
        Surface2Preview = Freeze(new SolidColorBrush(s2.Value));
        Surface3Preview = Freeze(new SolidColorBrush(s3.Value));
        OuterBorderPreview = Freeze(new SolidColorBrush(outerBorder.Value));
        InnerBorderPreview = Freeze(new SolidColorBrush(innerBorder.Value));

        // Persist + apply globally. Fire-and-forget: persistence is ~1ms (single SQLite row) and
        // a stray failure shouldn't block the UI; the user just sees their hex stuck and can retry.
        _ = _theme.SetAsync(bg.Value, fg.Value, dark.Value, fgDark.Value, del.Value,
            s1.Value, s2.Value, s3.Value, outerBorder.Value, innerBorder.Value);
    }

    private static Color? ParseOrNull(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try { return (Color)ColorConverter.ConvertFromString(s)!; }
        catch { return null; }
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
