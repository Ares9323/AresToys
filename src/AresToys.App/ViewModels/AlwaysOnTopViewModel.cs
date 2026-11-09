using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.AlwaysOnTop;
using AresToys.App.Services.Hotkeys;
using AresToys.Storage.Settings;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → Always on top tab: the look of the frame drawn around pinned
/// windows. Every change is saved to the settings store and pushed to the windows already
/// pinned through <see cref="AlwaysOnTopService.ReloadSettingsAsync"/>.</summary>
public sealed partial class AlwaysOnTopViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly AlwaysOnTopService _service;
    private readonly HotkeyConfigService _hotkeys;
    private CancellationTokenSource? _persistDelay;
    private bool _loading;

    public AlwaysOnTopViewModel(ISettingsStore store, AlwaysOnTopService service, HotkeyConfigService hotkeys)
    {
        _store = store;
        _service = service;
        _hotkeys = hotkeys;
    }

    /// <summary>Module switch, shown in App Settings > Modules; the page is hidden from the sidebar while off.</summary>
    [ObservableProperty] private bool _enabled = true;

    /// <summary>The shortcut chip on the page. It edits the hotkey of the built-in "Always on top" workflow, the
    /// same binding the Hotkeys &amp; Workflows page shows, so the two never disagree.</summary>
    [ObservableProperty] private HotkeyItemViewModel? _shortcut;

    [ObservableProperty] private bool _showFrame = true;

    [NotifyPropertyChangedFor(nameof(ColorPreview))]
    [ObservableProperty] private string _colorHex = AlwaysOnTopSettings.DefaultColorHex;

    [ObservableProperty] private int _thicknessPx = AlwaysOnTopSettings.Default.ThicknessPx;
    [NotifyPropertyChangedFor(nameof(ManualCorner))]
    [ObservableProperty] private bool _autoCorner = true;
    [ObservableProperty] private int _cornerRadiusPx = 12;
    [ObservableProperty] private int _opacityPercent = AlwaysOnTopSettings.Default.OpacityPercent;

    public bool ManualCorner => !AutoCorner;

    /// <summary>Swatch fill. Keeps the last valid colour while the hex box holds half-typed text.</summary>
    public Brush ColorPreview
    {
        get
        {
            var brush = new SolidColorBrush(AlwaysOnTopSettings.ParseColor(ColorHex));
            brush.Freeze();
            return brush;
        }
    }

    public void Dispose()
    {
        _persistDelay?.Cancel();
        _persistDelay?.Dispose();
    }

    public async Task LoadAsync()
    {
        var s = await AlwaysOnTopSettings.LoadAsync(_store, CancellationToken.None).ConfigureAwait(true);
        var enabled = await AlwaysOnTopSettings.LoadEnabledAsync(_store, CancellationToken.None).ConfigureAwait(true);
        await RefreshShortcutAsync().ConfigureAwait(true);
        _loading = true;
        try
        {
            Enabled = enabled;
            ShowFrame = s.ShowFrame;
            ColorHex = $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}";
            ThicknessPx = s.ThicknessPx;
            AutoCorner = s.CornerRadiusPx == AlwaysOnTopSettings.AutoCorner;
            CornerRadiusPx = AutoCorner ? CornerRadiusPx : s.CornerRadiusPx;
            OpacityPercent = s.OpacityPercent;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        var d = AlwaysOnTopSettings.Default;
        ShowFrame = d.ShowFrame;
        ColorHex = AlwaysOnTopSettings.DefaultColorHex;
        ThicknessPx = d.ThicknessPx;
        AutoCorner = true;
        OpacityPercent = d.OpacityPercent;
    }

    private async Task RefreshShortcutAsync()
    {
        const string id = AresToys.Pipeline.Profiles.DefaultPipelineProfiles.AlwaysOnTopId;
        var binding = await _hotkeys.GetEffectiveAsync(id, CancellationToken.None).ConfigureAwait(true);
        Shortcut = new HotkeyItemViewModel(
            id,
            AresToys.App.Resources.Strings.Sidebar_AlwaysOnTop,
            isBuiltIn: true,
            binding,
            _hotkeys,
            // The chip re-reads its own binding after a rebind; nothing else on this page shows it.
            refreshList: () => { },
            openInWorkflows: _ => { });
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = _service.SetEnabledAsync(value, CancellationToken.None);
    }

    partial void OnShowFrameChanged(bool value) => QueuePersist();
    partial void OnColorHexChanged(string value) => QueuePersist();
    partial void OnThicknessPxChanged(int value) => QueuePersist();
    partial void OnAutoCornerChanged(bool value) => QueuePersist();
    partial void OnCornerRadiusPxChanged(int value) => QueuePersist();
    partial void OnOpacityPercentChanged(int value) => QueuePersist();

    /// <summary>Sliders fire on every pixel of a drag; wait for a short pause before writing.</summary>
    private void QueuePersist()
    {
        if (_loading) return;
        _persistDelay?.Cancel();
        var cts = _persistDelay = new CancellationTokenSource();
        _ = PersistAsync(cts.Token);
    }

    private async Task PersistAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            if (!IsValidHex(ColorHex)) return;

            await _store.SetAsync(AlwaysOnTopSettings.KeyShowFrame, ShowFrame ? "1" : "0", false, token).ConfigureAwait(false);
            await _store.SetAsync(AlwaysOnTopSettings.KeyColor, ColorHex, false, token).ConfigureAwait(false);
            await _store.SetAsync(AlwaysOnTopSettings.KeyThickness, ThicknessPx.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            var radius = AutoCorner ? AlwaysOnTopSettings.AutoCorner : CornerRadiusPx;
            await _store.SetAsync(AlwaysOnTopSettings.KeyCornerRadius, radius.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            await _store.SetAsync(AlwaysOnTopSettings.KeyOpacity, OpacityPercent.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            await _service.ReloadSettingsAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this write.
        }
    }

    private static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#' || (hex.Length != 7 && hex.Length != 9)) return false;
        return uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }
}
