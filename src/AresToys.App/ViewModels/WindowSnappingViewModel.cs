using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Zones;
using AresToys.Storage.Settings;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → Window snapping tab: the on and off switch, the button that opens
/// the layout editor, and the colors and shape of the overlay shown while dragging with Shift.
/// Appearance changes are saved to the settings store and pushed to the running drag service.</summary>
public sealed partial class WindowSnappingViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly WindowSnappingService _service;
    private CancellationTokenSource? _persistDelay;
    private bool _loading;

    public WindowSnappingViewModel(ISettingsStore store, WindowSnappingService service)
    {
        _store = store;
        _service = service;
        _service.EnabledChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _loading = true;
            try { Enabled = _service.IsEnabled; }
            finally { _loading = false; }
        });
    }

    [ObservableProperty] private bool _enabled;

    [NotifyPropertyChangedFor(nameof(ZoneColorPreview))]
    [ObservableProperty] private string _zoneColorHex = "#9323AE";

    [NotifyPropertyChangedFor(nameof(HighlightColorPreview))]
    [ObservableProperty] private string _highlightColorHex = "#9323AE";

    [NotifyPropertyChangedFor(nameof(BorderColorPreview))]
    [ObservableProperty] private string _borderColorHex = "#9323AE";

    [ObservableProperty] private int _zoneOpacity = ZoneAppearance.Default.FillOpacityPercent;
    [ObservableProperty] private int _highlightOpacity = ZoneAppearance.Default.HighlightOpacityPercent;
    [ObservableProperty] private int _borderThickness = ZoneAppearance.Default.BorderThickness;
    [ObservableProperty] private int _cornerRadius = ZoneAppearance.Default.CornerRadius;

    // Drag behavior (PowerToys "Zone behavior")
    [ObservableProperty] private bool _shiftToActivate = true;
    [ObservableProperty] private bool _nonPrimaryMouseToggle;
    [ObservableProperty] private bool _middleClickSpanning;
    [ObservableProperty] private bool _showOnAllMonitors = true;
    [ObservableProperty] private int _overlapAlgorithmIndex = (int)ZoneOverlapAlgorithm.ClosestCenter;

    // Appearance
    [NotifyPropertyChangedFor(nameof(IsCustomStyle))]
    [ObservableProperty] private int _zoneStyleIndex = (int)ZoneAppearanceStyle.Custom;
    [ObservableProperty] private bool _showZoneNumber = true;

    // Window behavior
    [ObservableProperty] private bool _restoreSizeOnUnsnap = true;
    [ObservableProperty] private bool _transparentWhileDragging = true;
    [ObservableProperty] private bool _allowChildWindows = true;
    [ObservableProperty] private bool _disableRoundedCorners = true;

    // Window memory
    [ObservableProperty] private bool _keepInZonesOnDisplayChange = true;
    [ObservableProperty] private bool _reapplyOnLayoutChange;
    [ObservableProperty] private bool _moveNewWindowsToLastZone;
    [ObservableProperty] private bool _moveNewWindowsToActiveMonitor;

    // Keyboard snapping (PowerToys "Override Windows Snap"); the index maps 0 RelativePosition, 1 ZoneIndex
    [ObservableProperty] private bool _overrideWindowsSnap;
    [ObservableProperty] private int _moveBasedOnIndex = (int)ZoneMoveBasis.RelativePosition;
    [ObservableProperty] private bool _moveAcrossMonitors = true;

    // Quick layout switch (PowerToys "Enable quick layout switch")
    [ObservableProperty] private bool _quickLayoutSwitch = true;

    // Multiple monitors: one combined zone area, and the display the layout editor opens on
    // (the index maps 0 CursorMonitor, 1 PrimaryMonitor, 2 ActiveWindowMonitor)
    [ObservableProperty] private bool _spanZonesAcrossMonitors;
    [ObservableProperty] private int _launchEditorOnIndex = (int)ZoneEditorDisplay.CursorMonitor;

    // Excluded apps, one exe name per line
    [ObservableProperty] private string _excludedAppsText = string.Empty;

    public bool IsCustomStyle => ZoneStyleIndex == (int)ZoneAppearanceStyle.Custom;

    public Brush ZoneColorPreview => Preview(ZoneColorHex);
    public Brush HighlightColorPreview => Preview(HighlightColorHex);
    public Brush BorderColorPreview => Preview(BorderColorHex);

    public void Dispose()
    {
        _persistDelay?.Cancel();
        _persistDelay?.Dispose();
    }

    public async Task LoadAsync()
    {
        var a = await ZoneAppearance.LoadAsync(_store, CancellationToken.None).ConfigureAwait(true);
        _loading = true;
        try
        {
            Enabled = _service.IsEnabled;
            var o = await WindowSnappingSettings.LoadAsync(_store, CancellationToken.None).ConfigureAwait(true);
            ShiftToActivate = o.ShiftToActivate;
            NonPrimaryMouseToggle = o.NonPrimaryMouseToggle;
            MiddleClickSpanning = o.MiddleClickSpanning;
            ShowOnAllMonitors = o.ShowOnAllMonitors;
            OverlapAlgorithmIndex = (int)o.OverlapAlgorithm;
            ZoneStyleIndex = (int)o.ZoneStyle;
            ShowZoneNumber = o.ShowZoneNumber;
            RestoreSizeOnUnsnap = o.RestoreSizeOnUnsnap;
            TransparentWhileDragging = o.TransparentWhileDragging;
            AllowChildWindows = o.AllowChildWindows;
            DisableRoundedCorners = o.DisableRoundedCorners;
            KeepInZonesOnDisplayChange = o.KeepInZonesOnDisplayChange;
            ReapplyOnLayoutChange = o.ReapplyOnLayoutChange;
            MoveNewWindowsToLastZone = o.MoveNewWindowsToLastZone;
            MoveNewWindowsToActiveMonitor = o.MoveNewWindowsToActiveMonitor;
            OverrideWindowsSnap = o.OverrideWindowsSnap;
            MoveBasedOnIndex = (int)o.MoveBasedOn;
            MoveAcrossMonitors = o.MoveAcrossMonitors;
            QuickLayoutSwitch = o.QuickLayoutSwitch;
            SpanZonesAcrossMonitors = o.SpanZonesAcrossMonitors;
            LaunchEditorOnIndex = (int)o.LaunchEditorOn;
            ExcludedAppsText = WindowSnappingSettings.FormatApps(o.ExcludedApps);
            ZoneColorHex = Hex(a.Fill);
            HighlightColorHex = Hex(a.HighlightFill);
            BorderColorHex = Hex(a.Border);
            ZoneOpacity = a.FillOpacityPercent;
            HighlightOpacity = a.HighlightOpacityPercent;
            BorderThickness = a.BorderThickness;
            CornerRadius = a.CornerRadius;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void EditLayouts() => _service.OpenEditor();

    [RelayCommand]
    private void ResetToDefaults()
    {
        var d = ZoneAppearance.Default;
        var o = WindowSnappingSettings.Default;
        ShiftToActivate = o.ShiftToActivate;
        NonPrimaryMouseToggle = o.NonPrimaryMouseToggle;
        MiddleClickSpanning = o.MiddleClickSpanning;
        ShowOnAllMonitors = o.ShowOnAllMonitors;
        OverlapAlgorithmIndex = (int)o.OverlapAlgorithm;
        ZoneStyleIndex = (int)o.ZoneStyle;
        ShowZoneNumber = o.ShowZoneNumber;
        RestoreSizeOnUnsnap = o.RestoreSizeOnUnsnap;
        TransparentWhileDragging = o.TransparentWhileDragging;
        AllowChildWindows = o.AllowChildWindows;
        DisableRoundedCorners = o.DisableRoundedCorners;
        KeepInZonesOnDisplayChange = o.KeepInZonesOnDisplayChange;
        ReapplyOnLayoutChange = o.ReapplyOnLayoutChange;
        MoveNewWindowsToLastZone = o.MoveNewWindowsToLastZone;
        MoveNewWindowsToActiveMonitor = o.MoveNewWindowsToActiveMonitor;
        OverrideWindowsSnap = o.OverrideWindowsSnap;
        MoveBasedOnIndex = (int)o.MoveBasedOn;
        MoveAcrossMonitors = o.MoveAcrossMonitors;
        QuickLayoutSwitch = o.QuickLayoutSwitch;
        SpanZonesAcrossMonitors = o.SpanZonesAcrossMonitors;
        LaunchEditorOnIndex = (int)o.LaunchEditorOn;
        ExcludedAppsText = WindowSnappingSettings.FormatApps(o.ExcludedApps);
        ZoneColorHex = Hex(d.Fill);
        HighlightColorHex = Hex(d.HighlightFill);
        BorderColorHex = Hex(d.Border);
        ZoneOpacity = d.FillOpacityPercent;
        HighlightOpacity = d.HighlightOpacityPercent;
        BorderThickness = d.BorderThickness;
        CornerRadius = d.CornerRadius;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = _service.SetEnabledAsync(value);
    }

    partial void OnShiftToActivateChanged(bool value) => QueuePersist();
    partial void OnNonPrimaryMouseToggleChanged(bool value) => QueuePersist();
    partial void OnMiddleClickSpanningChanged(bool value) => QueuePersist();
    partial void OnShowOnAllMonitorsChanged(bool value) => QueuePersist();
    partial void OnOverlapAlgorithmIndexChanged(int value) => QueuePersist();
    partial void OnZoneStyleIndexChanged(int value) => QueuePersist();
    partial void OnShowZoneNumberChanged(bool value) => QueuePersist();
    partial void OnRestoreSizeOnUnsnapChanged(bool value) => QueuePersist();
    partial void OnTransparentWhileDraggingChanged(bool value) => QueuePersist();
    partial void OnAllowChildWindowsChanged(bool value) => QueuePersist();
    partial void OnDisableRoundedCornersChanged(bool value) => QueuePersist();
    partial void OnKeepInZonesOnDisplayChangeChanged(bool value) => QueuePersist();
    partial void OnReapplyOnLayoutChangeChanged(bool value) => QueuePersist();
    partial void OnMoveNewWindowsToLastZoneChanged(bool value) => QueuePersist();
    partial void OnMoveNewWindowsToActiveMonitorChanged(bool value) => QueuePersist();
    partial void OnOverrideWindowsSnapChanged(bool value) => QueuePersist();
    partial void OnMoveBasedOnIndexChanged(int value) => QueuePersist();
    partial void OnMoveAcrossMonitorsChanged(bool value) => QueuePersist();
    partial void OnQuickLayoutSwitchChanged(bool value) => QueuePersist();
    partial void OnSpanZonesAcrossMonitorsChanged(bool value) => QueuePersist();
    partial void OnLaunchEditorOnIndexChanged(int value) => QueuePersist();
    partial void OnExcludedAppsTextChanged(string value) => QueuePersist();
    partial void OnZoneColorHexChanged(string value) => QueuePersist();
    partial void OnHighlightColorHexChanged(string value) => QueuePersist();
    partial void OnBorderColorHexChanged(string value) => QueuePersist();
    partial void OnZoneOpacityChanged(int value) => QueuePersist();
    partial void OnHighlightOpacityChanged(int value) => QueuePersist();
    partial void OnBorderThicknessChanged(int value) => QueuePersist();
    partial void OnCornerRadiusChanged(int value) => QueuePersist();

    /// <summary>Sliders fire on every step of a drag; wait for a short pause before writing.</summary>
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
            if (!IsValidHex(ZoneColorHex) || !IsValidHex(HighlightColorHex) || !IsValidHex(BorderColorHex)) return;

            await _store.SetAsync(ZoneAppearance.KeyZoneColor, ZoneColorHex, false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyHighlightColor, HighlightColorHex, false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyBorderColor, BorderColorHex, false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyZoneOpacity, ZoneOpacity.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyHighlightOpacity, HighlightOpacity.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyBorderThickness, BorderThickness.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            await _store.SetAsync(ZoneAppearance.KeyCornerRadius, CornerRadius.ToString(CultureInfo.InvariantCulture), false, token).ConfigureAwait(false);
            var options = new WindowSnappingSettings(
                ShiftToActivate, NonPrimaryMouseToggle, MiddleClickSpanning, ShowOnAllMonitors,
                (ZoneOverlapAlgorithm)Math.Clamp(OverlapAlgorithmIndex, 0, 3),
                (ZoneAppearanceStyle)Math.Clamp(ZoneStyleIndex, 0, 1),
                ShowZoneNumber, RestoreSizeOnUnsnap, TransparentWhileDragging, AllowChildWindows,
                DisableRoundedCorners, WindowSnappingSettings.ParseApps(ExcludedAppsText),
                KeepInZonesOnDisplayChange, ReapplyOnLayoutChange, MoveNewWindowsToLastZone, MoveNewWindowsToActiveMonitor,
                OverrideWindowsSnap, (ZoneMoveBasis)Math.Clamp(MoveBasedOnIndex, 0, 1), MoveAcrossMonitors,
                QuickLayoutSwitch, SpanZonesAcrossMonitors,
                (ZoneEditorDisplay)Math.Clamp(LaunchEditorOnIndex, 0, 2));
            await options.SaveAsync(_store, token).ConfigureAwait(false);
            await _service.ReloadSettingsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this write.
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool IsValidHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#' || (hex.Length != 7 && hex.Length != 9)) return false;
        return uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }

    private static Brush Preview(string hex)
    {
        Color color = Colors.Transparent;
        if (IsValidHex(hex))
        {
            try { color = (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { }
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
