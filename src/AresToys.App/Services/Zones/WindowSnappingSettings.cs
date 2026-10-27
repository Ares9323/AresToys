using AresToys.Storage.Settings;

namespace AresToys.App.Services.Zones;

/// <summary>How the zone under the cursor is chosen when several zones remain candidates
/// (PowerToys "when multiple zones overlap").</summary>
public enum ZoneOverlapAlgorithm
{
    /// <summary>The candidate with the smallest area.</summary>
    Smallest,

    /// <summary>The candidate with the largest area.</summary>
    Largest,

    /// <summary>The cursor position inside the shared region picks the candidate (left to right, or top to bottom).</summary>
    Positional,

    /// <summary>The candidate whose center is nearest the cursor.</summary>
    ClosestCenter,
}

/// <summary>How the keyboard shortcuts of "Override Windows Snap" pick the next zone.</summary>
public enum ZoneMoveBasis
{
    /// <summary>By geometry: the zone that lies in the pressed direction from the window.</summary>
    RelativePosition,

    /// <summary>By zone number: left and up go to the previous zone, right and down to the next one.</summary>
    ZoneIndex,
}

/// <summary>Which colors the zone overlay uses.</summary>
public enum ZoneAppearanceStyle
{
    /// <summary>Windows accent color for the highlight and border, neutral gray for the other zones.</summary>
    WindowsDefault,

    /// <summary>The colors saved under <c>zones.appearance.*</c>.</summary>
    Custom,
}

/// <summary>Which display the layout editor opens on (PowerToys "launch editor on the display").</summary>
public enum ZoneEditorDisplay
{
    /// <summary>The monitor under the mouse pointer.</summary>
    CursorMonitor,

    /// <summary>The primary monitor.</summary>
    PrimaryMonitor,

    /// <summary>The monitor of the foreground window.</summary>
    ActiveWindowMonitor,
}

/// <summary>The options of Window snapping, read by <see cref="ZoneDragService"/> (which hands them on to
/// <see cref="ZoneWindowTracker"/> for the four window memory options).
/// Every field has a settings key under <c>windowsnapping.</c>; a missing or unparsable value falls back to
/// the default, so a damaged setting never breaks snapping. Booleans are stored as "true" or "false",
/// enums by name, the excluded apps as lines separated by a line feed.</summary>
public sealed record WindowSnappingSettings(
    bool ShiftToActivate,
    bool NonPrimaryMouseToggle,
    bool MiddleClickSpanning,
    bool ShowOnAllMonitors,
    ZoneOverlapAlgorithm OverlapAlgorithm,
    ZoneAppearanceStyle ZoneStyle,
    bool ShowZoneNumber,
    bool RestoreSizeOnUnsnap,
    bool TransparentWhileDragging,
    bool AllowChildWindows,
    bool DisableRoundedCorners,
    IReadOnlyList<string> ExcludedApps,
    bool KeepInZonesOnDisplayChange,
    bool ReapplyOnLayoutChange,
    bool MoveNewWindowsToLastZone,
    bool MoveNewWindowsToActiveMonitor,
    bool OverrideWindowsSnap,
    ZoneMoveBasis MoveBasedOn,
    bool MoveAcrossMonitors,
    bool QuickLayoutSwitch,
    bool SpanZonesAcrossMonitors,
    ZoneEditorDisplay LaunchEditorOn)
{
    public const string KeyShiftToActivate = "windowsnapping.shift_to_activate";
    public const string KeyNonPrimaryMouseToggle = "windowsnapping.mouse_button_toggle";
    public const string KeyMiddleClickSpanning = "windowsnapping.middle_click_spanning";
    public const string KeyShowOnAllMonitors = "windowsnapping.show_all_monitors";
    public const string KeyOverlapAlgorithm = "windowsnapping.overlap_algorithm";
    public const string KeyZoneStyle = "windowsnapping.zone_style";
    public const string KeyShowZoneNumber = "windowsnapping.show_zone_number";
    public const string KeyRestoreSize = "windowsnapping.restore_size";
    public const string KeyTransparentWhileDragging = "windowsnapping.drag_transparent";
    public const string KeyAllowChildWindows = "windowsnapping.allow_child_windows";
    public const string KeyDisableRoundedCorners = "windowsnapping.disable_rounded_corners";
    public const string KeyExcludedApps = "windowsnapping.excluded_apps";
    public const string KeyKeepInZonesOnDisplayChange = "windowsnapping.keep_on_display_change";
    public const string KeyReapplyOnLayoutChange = "windowsnapping.reapply_on_layout_change";
    public const string KeyMoveNewWindowsToLastZone = "windowsnapping.new_windows_last_zone";
    public const string KeyMoveNewWindowsToActiveMonitor = "windowsnapping.new_windows_active_monitor";

    public const string KeyOverrideWindowsSnap = "windowsnapping.override_win_snap";
    public const string KeyMoveBasedOn = "windowsnapping.move_based_on";
    public const string KeyMoveAcrossMonitors = "windowsnapping.move_across_monitors";
    public const string KeyQuickLayoutSwitch = "windowsnapping.quick_layout_switch";
    public const string KeySpanZonesAcrossMonitors = "windowsnapping.span_monitors";
    public const string KeyLaunchEditorOn = "windowsnapping.launch_editor_on";

    public static readonly WindowSnappingSettings Default = new(
        ShiftToActivate: true,
        NonPrimaryMouseToggle: false,
        MiddleClickSpanning: false,
        ShowOnAllMonitors: true,
        OverlapAlgorithm: ZoneOverlapAlgorithm.ClosestCenter,
        ZoneStyle: ZoneAppearanceStyle.Custom,
        ShowZoneNumber: true,
        RestoreSizeOnUnsnap: true,
        TransparentWhileDragging: true,
        AllowChildWindows: true,
        DisableRoundedCorners: true,
        ExcludedApps: [],
        KeepInZonesOnDisplayChange: true,
        ReapplyOnLayoutChange: false,
        MoveNewWindowsToLastZone: false,
        MoveNewWindowsToActiveMonitor: false,
        OverrideWindowsSnap: false,
        MoveBasedOn: ZoneMoveBasis.RelativePosition,
        MoveAcrossMonitors: true,
        QuickLayoutSwitch: true,
        SpanZonesAcrossMonitors: false,
        LaunchEditorOn: ZoneEditorDisplay.CursorMonitor);

    public static async Task<WindowSnappingSettings> LoadAsync(ISettingsStore store, CancellationToken ct)
    {
        var d = Default;
        return new WindowSnappingSettings(
            ParseBool(await store.GetAsync(KeyShiftToActivate, ct).ConfigureAwait(false), d.ShiftToActivate),
            ParseBool(await store.GetAsync(KeyNonPrimaryMouseToggle, ct).ConfigureAwait(false), d.NonPrimaryMouseToggle),
            ParseBool(await store.GetAsync(KeyMiddleClickSpanning, ct).ConfigureAwait(false), d.MiddleClickSpanning),
            ParseBool(await store.GetAsync(KeyShowOnAllMonitors, ct).ConfigureAwait(false), d.ShowOnAllMonitors),
            ParseEnum(await store.GetAsync(KeyOverlapAlgorithm, ct).ConfigureAwait(false), d.OverlapAlgorithm),
            ParseEnum(await store.GetAsync(KeyZoneStyle, ct).ConfigureAwait(false), d.ZoneStyle),
            ParseBool(await store.GetAsync(KeyShowZoneNumber, ct).ConfigureAwait(false), d.ShowZoneNumber),
            ParseBool(await store.GetAsync(KeyRestoreSize, ct).ConfigureAwait(false), d.RestoreSizeOnUnsnap),
            ParseBool(await store.GetAsync(KeyTransparentWhileDragging, ct).ConfigureAwait(false), d.TransparentWhileDragging),
            ParseBool(await store.GetAsync(KeyAllowChildWindows, ct).ConfigureAwait(false), d.AllowChildWindows),
            ParseBool(await store.GetAsync(KeyDisableRoundedCorners, ct).ConfigureAwait(false), d.DisableRoundedCorners),
            ParseApps(await store.GetAsync(KeyExcludedApps, ct).ConfigureAwait(false)),
            ParseBool(await store.GetAsync(KeyKeepInZonesOnDisplayChange, ct).ConfigureAwait(false), d.KeepInZonesOnDisplayChange),
            ParseBool(await store.GetAsync(KeyReapplyOnLayoutChange, ct).ConfigureAwait(false), d.ReapplyOnLayoutChange),
            ParseBool(await store.GetAsync(KeyMoveNewWindowsToLastZone, ct).ConfigureAwait(false), d.MoveNewWindowsToLastZone),
            ParseBool(await store.GetAsync(KeyMoveNewWindowsToActiveMonitor, ct).ConfigureAwait(false), d.MoveNewWindowsToActiveMonitor),
            ParseBool(await store.GetAsync(KeyOverrideWindowsSnap, ct).ConfigureAwait(false), d.OverrideWindowsSnap),
            ParseEnum(await store.GetAsync(KeyMoveBasedOn, ct).ConfigureAwait(false), d.MoveBasedOn),
            ParseBool(await store.GetAsync(KeyMoveAcrossMonitors, ct).ConfigureAwait(false), d.MoveAcrossMonitors),
            ParseBool(await store.GetAsync(KeyQuickLayoutSwitch, ct).ConfigureAwait(false), d.QuickLayoutSwitch),
            ParseBool(await store.GetAsync(KeySpanZonesAcrossMonitors, ct).ConfigureAwait(false), d.SpanZonesAcrossMonitors),
            ParseEnum(await store.GetAsync(KeyLaunchEditorOn, ct).ConfigureAwait(false), d.LaunchEditorOn));
    }

    /// <summary>Writes every field. The Settings tab calls this and then
    /// <see cref="WindowSnappingService.ReloadSettingsAsync"/>.</summary>
    public async Task SaveAsync(ISettingsStore store, CancellationToken ct)
    {
        await store.SetAsync(KeyShiftToActivate, ToValue(ShiftToActivate), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyNonPrimaryMouseToggle, ToValue(NonPrimaryMouseToggle), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyMiddleClickSpanning, ToValue(MiddleClickSpanning), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyShowOnAllMonitors, ToValue(ShowOnAllMonitors), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyOverlapAlgorithm, OverlapAlgorithm.ToString(), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyZoneStyle, ZoneStyle.ToString(), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyShowZoneNumber, ToValue(ShowZoneNumber), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyRestoreSize, ToValue(RestoreSizeOnUnsnap), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyTransparentWhileDragging, ToValue(TransparentWhileDragging), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyAllowChildWindows, ToValue(AllowChildWindows), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyDisableRoundedCorners, ToValue(DisableRoundedCorners), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyExcludedApps, FormatApps(ExcludedApps), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyKeepInZonesOnDisplayChange, ToValue(KeepInZonesOnDisplayChange), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyReapplyOnLayoutChange, ToValue(ReapplyOnLayoutChange), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyMoveNewWindowsToLastZone, ToValue(MoveNewWindowsToLastZone), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyMoveNewWindowsToActiveMonitor, ToValue(MoveNewWindowsToActiveMonitor), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyOverrideWindowsSnap, ToValue(OverrideWindowsSnap), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyMoveBasedOn, MoveBasedOn.ToString(), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyMoveAcrossMonitors, ToValue(MoveAcrossMonitors), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyQuickLayoutSwitch, ToValue(QuickLayoutSwitch), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeySpanZonesAcrossMonitors, ToValue(SpanZonesAcrossMonitors), false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyLaunchEditorOn, LaunchEditorOn.ToString(), false, ct).ConfigureAwait(false);
    }

    /// <summary>True when the exe file name (with or without ".exe", any case) is in <see cref="ExcludedApps"/>.</summary>
    public bool IsExcluded(string? exeFileName)
    {
        if (ExcludedApps.Count == 0 || string.IsNullOrWhiteSpace(exeFileName)) return false;
        var name = StripExe(exeFileName.Trim());
        foreach (var app in ExcludedApps)
        {
            if (string.Equals(StripExe(app), name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Splits the stored text into trimmed, non empty names (one per line; a CR is ignored).</summary>
    public static IReadOnlyList<string> ParseApps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The stored text for a list of names: one per line.</summary>
    public static string FormatApps(IEnumerable<string> apps)
        => string.Join('\n', apps.Select(a => a.Trim()).Where(a => a.Length > 0));

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string ToValue(bool value) => value ? "true" : "false";

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var value = raw.Trim();
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
        return fallback;
    }

    private static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(raw?.Trim(), true, out var value) && Enum.IsDefined(value) ? value : fallback;
}
