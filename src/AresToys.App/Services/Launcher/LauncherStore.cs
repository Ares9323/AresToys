using System.Text.Json;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Launcher;

/// <summary>Persists the entire launcher state (cells + tab titles) as a single JSON blob
/// under "launcher.state". Old "launcher.cells" key is read for migration but no longer
/// written. Load always returns the full <see cref="LauncherState"/> with every cell slot
/// filled (empty for unconfigured), so callers don't juggle missing-key cases.</summary>
public sealed class LauncherStore
{
    private const string SettingsKey = "launcher.state";
    private const string LegacySettingsKey = "launcher.cells";
    /// <summary>Last user-selected tab (e.g. "3"). Restored on next launcher open so the user
    /// returns to whatever they were working on. Stored separately from the cells blob so
    /// updating the active-tab marker doesn't rewrite the (potentially large) cell payload.</summary>
    private const string ActiveTabKey = "launcher.active_tab";
    /// <summary>Window geometry — size and on-screen position. JSON blob so the four numbers
    /// commit atomically (a partial save would put the launcher in a contradictory layout).</summary>
    private const string GeometryKey = "launcher.geometry";
    /// <summary>Last drag-mode toggle state. Saved on every hide so closing the launcher while
    /// in drag mode reopens it the same way (the user was clearly in the middle of editing).</summary>
    private const string DragModeKey = "launcher.drag_mode";
    /// <summary>Per-user cell sizing — icon edge length and label point-size in WPF DIPs. The
    /// drag-mode panel exposes both via sliders; defaults match the original hardcoded values
    /// (40 px icon / 11 pt label).</summary>
    private const string IconSizeKey = "launcher.icon_size";
    private const string LabelFontSizeKey = "launcher.label_font_size";

    public const double DefaultIconSize = 40;
    public const double DefaultLabelFontSize = 11;
    public const double MinIconSize = 24;
    public const double MaxIconSize = 96;
    public const double MinLabelFontSize = 8;
    public const double MaxLabelFontSize = 18;

    private readonly ISettingsStore _settings;
    private long _stateVersion;

    public LauncherStore(ISettingsStore settings) { _settings = settings; }

    /// <summary>Monotonic version bumped on every cell/tab-title mutation. Read-only consumers
    /// (LauncherWindow) snapshot the value after a successful load and skip subsequent reloads
    /// when the store hasn't changed since. Volatile read so a save on the UI thread is visible
    /// to the next read on the same thread without needing a fence — there's no cross-thread
    /// race here in practice (the launcher window runs everything on the dispatcher).</summary>
    public long StateVersion => System.Threading.Interlocked.Read(ref _stateVersion);

    /// <summary>Force the state version to advance without going through <see cref="SaveAsync"/>.
    /// Used after a backup import writes the launcher settings key directly through
    /// <see cref="ISettingsStore"/> — the store would otherwise stay at its old version and the
    /// next <c>PrepareAsync</c> on a pre-warmed <c>LauncherWindow</c> would skip the reload,
    /// leaving the visible grid stale (the "empty launcher after import" bug).</summary>
    public void BumpStateVersion() => System.Threading.Interlocked.Increment(ref _stateVersion);

    public async Task<LauncherState> LoadAsync(CancellationToken cancellationToken)
    {
        var cells = new Dictionary<string, LauncherCell>(StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var raw = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<StateDto>(raw);
                if (dto is not null)
                {
                    if (dto.Cells is not null)
                    {
                        foreach (var c in dto.Cells)
                        {
                            if (string.IsNullOrEmpty(c.TabKey) || string.IsNullOrEmpty(c.KeyChar)) continue;
                            // Older blobs (before window-mode / admin / title fields) just
                            // omit the new keys; deserialisation gives them their defaults
                            // (Normal mode, false admin, empty title/process), which match
                            // the LauncherCell positional defaults — so no migration needed.
                            var mode = Enum.TryParse<LauncherWindowMode>(c.WindowMode, ignoreCase: true, out var m)
                                ? m : LauncherWindowMode.Normal;
                            var cell = new LauncherCell(
                                c.TabKey, c.KeyChar,
                                c.Label ?? string.Empty,
                                c.Path  ?? string.Empty,
                                c.Args  ?? string.Empty,
                                RunAsAdmin: c.RunAsAdmin ?? false,
                                WindowMode: mode,
                                WindowTitle: c.WindowTitle ?? string.Empty,
                                ProcessName: c.ProcessName ?? string.Empty,
                                IconPath: c.IconPath ?? string.Empty,
                                IconIndex: c.IconIndex ?? 0,
                                WorkflowId: c.WorkflowId ?? string.Empty);
                            cells[cell.ComposedKey] = cell;
                        }
                    }
                    if (dto.TabTitles is not null)
                    {
                        foreach (var kv in dto.TabTitles)
                        {
                            if (!string.IsNullOrEmpty(kv.Key)) titles[kv.Key] = kv.Value ?? string.Empty;
                        }
                    }
                }
            }
            catch (JsonException) { /* ignore malformed and rebuild from defaults */ }
        }
        else
        {
            // Backward-compat: a previous build wrote "launcher.cells" as a flat list with no
            // tab info. Migrate those into tab "1" so the user doesn't lose their entries.
            await TryMigrateLegacyAsync(cells, cancellationToken).ConfigureAwait(false);
        }

        // Always materialise the full layout — every F-key slot + every (tab, key) pair gets a
        // LauncherCell, defaulting to Empty when not yet configured. The window just iterates.
        EnsureSlot(cells, LauncherTabs.FunctionStrip, LauncherKeyboardLayout.FunctionKeys);
        foreach (var t in LauncherKeyboardLayout.TabKeys)
            EnsureSlot(cells, t, LauncherKeyboardLayout.AllTabKeyChars());

        return new LauncherState(cells, titles);
    }

    public async Task SaveAsync(LauncherState state, CancellationToken cancellationToken)
    {
        // Persist only configured cells + non-empty titles to keep the JSON small.
        var dto = new StateDto
        {
            Cells = state.Cells.Values.Where(c => c.IsConfigured)
                .Select(c => new CellDto(c.TabKey, c.KeyChar, c.Label, c.Path, c.Args)
                {
                    // Only emit the extra fields when they're non-default — keeps the JSON
                    // blob compact and easy to read for users who never touch advanced opts.
                    RunAsAdmin   = c.RunAsAdmin ? true : null,
                    WindowMode   = c.WindowMode == LauncherWindowMode.Normal ? null : c.WindowMode.ToString(),
                    WindowTitle  = string.IsNullOrWhiteSpace(c.WindowTitle) ? null : c.WindowTitle,
                    ProcessName  = string.IsNullOrWhiteSpace(c.ProcessName) ? null : c.ProcessName,
                    IconPath     = string.IsNullOrWhiteSpace(c.IconPath) ? null : c.IconPath,
                    IconIndex    = c.IconIndex == 0 ? null : c.IconIndex,
                    WorkflowId   = string.IsNullOrWhiteSpace(c.WorkflowId) ? null : c.WorkflowId,
                })
                .ToList(),
            TabTitles = state.TabTitles
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        var json = JsonSerializer.Serialize(dto);
        await _settings.SetAsync(SettingsKey, json, sensitive: false, cancellationToken).ConfigureAwait(false);
        System.Threading.Interlocked.Increment(ref _stateVersion);
    }

    public async Task UpdateCellAsync(LauncherCell cell, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var cells = new Dictionary<string, LauncherCell>(state.Cells, StringComparer.OrdinalIgnoreCase);
        cells[cell.ComposedKey] = cell;
        // Funnels through SaveAsync, so the version bump happens there.
        await SaveAsync(new LauncherState(cells, state.TabTitles), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> LoadActiveTabAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(ActiveTabKey, cancellationToken).ConfigureAwait(false);
        // Accept only known tab keys — anything else falls back to caller's default. Guards
        // against a corrupted setting from manually-edited storage.
        return LauncherKeyboardLayout.TabKeys.Contains(raw, StringComparer.OrdinalIgnoreCase) ? raw : null;
    }

    public Task SaveActiveTabAsync(string tabKey, CancellationToken cancellationToken)
        => _settings.SetAsync(ActiveTabKey, tabKey, sensitive: false, cancellationToken);

    public async Task<LauncherGeometry?> LoadGeometryAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(GeometryKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<GeometryDto>(raw);
            if (dto is null) return null;
            return new LauncherGeometry(dto.W, dto.H, dto.L, dto.T);
        }
        catch (JsonException) { return null; }
    }

    public Task SaveGeometryAsync(LauncherGeometry g, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new GeometryDto(g.Width, g.Height, g.Left, g.Top));
        return _settings.SetAsync(GeometryKey, json, sensitive: false, cancellationToken);
    }

    /// <summary>Load the persisted drag-mode flag. False is the safe default: a brand-new
    /// install opens the launcher in normal mode (drag mode is the editing mode).</summary>
    public async Task<bool> LoadDragModeAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(DragModeKey, cancellationToken).ConfigureAwait(false);
        return string.Equals(raw, "1", StringComparison.Ordinal);
    }

    public Task SaveDragModeAsync(bool dragMode, CancellationToken cancellationToken)
        => _settings.SetAsync(DragModeKey, dragMode ? "1" : "0", sensitive: false, cancellationToken);

    /// <summary>Load icon / label sizes. Out-of-range or missing values fall back to defaults
    /// — the slider's Min/Max already constrain user input, so we don't need a second clamp at
    /// callers.</summary>
    public async Task<(double IconSize, double LabelFontSize)> LoadSizingAsync(CancellationToken cancellationToken)
    {
        var rawIcon = await _settings.GetAsync(IconSizeKey, cancellationToken).ConfigureAwait(false);
        var rawLabel = await _settings.GetAsync(LabelFontSizeKey, cancellationToken).ConfigureAwait(false);
        var icon = ParseInRange(rawIcon, DefaultIconSize, MinIconSize, MaxIconSize);
        var label = ParseInRange(rawLabel, DefaultLabelFontSize, MinLabelFontSize, MaxLabelFontSize);
        return (icon, label);
    }

    public Task SaveIconSizeAsync(double iconSize, CancellationToken cancellationToken)
        => _settings.SetAsync(IconSizeKey,
            Math.Clamp(iconSize, MinIconSize, MaxIconSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken);

    public Task SaveLabelFontSizeAsync(double labelFontSize, CancellationToken cancellationToken)
        => _settings.SetAsync(LabelFontSizeKey,
            Math.Clamp(labelFontSize, MinLabelFontSize, MaxLabelFontSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken);

    private static double ParseInRange(string? raw, double @default, double min, double max)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return @default;
        return Math.Clamp(v, min, max);
    }

    private sealed record GeometryDto(double W, double H, double L, double T);

    public async Task UpdateTabTitleAsync(string tabKey, string title, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var titles = new Dictionary<string, string>(state.TabTitles, StringComparer.OrdinalIgnoreCase)
        {
            [tabKey] = title.Trim()
        };
        await SaveAsync(new LauncherState(state.Cells, titles), cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSlot(Dictionary<string, LauncherCell> cells, string tabKey, IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            var composed = LauncherCell.ComposeKey(tabKey, k);
            if (!cells.ContainsKey(composed)) cells[composed] = LauncherCell.Empty(tabKey, k);
        }
    }

    private async Task TryMigrateLegacyAsync(Dictionary<string, LauncherCell> cells, CancellationToken cancellationToken)
    {
        var legacy = await _settings.GetAsync(LegacySettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(legacy)) return;
        try
        {
            var oldDtos = JsonSerializer.Deserialize<List<LegacyDto>>(legacy);
            if (oldDtos is null) return;
            // Drop the legacy entries onto the first user tab so they survive the upgrade.
            const string defaultTab = "1";
            foreach (var d in oldDtos)
            {
                if (string.IsNullOrEmpty(d.Key)) continue;
                var cell = new LauncherCell(defaultTab, d.Key, d.Label ?? string.Empty,
                    d.Path ?? string.Empty, d.Args ?? string.Empty);
                cells[cell.ComposedKey] = cell;
            }
        }
        catch (JsonException) { /* ignore corrupt legacy data */ }
    }

    private sealed record CellDto(string TabKey, string KeyChar, string? Label, string? Path, string? Args)
    {
        public bool? RunAsAdmin { get; init; }
        public string? WindowMode { get; init; }
        public string? WindowTitle { get; init; }
        public string? ProcessName { get; init; }
        public string? IconPath { get; init; }
        public int? IconIndex { get; init; }
        public string? WorkflowId { get; init; }
    }
    private sealed class StateDto
    {
        public List<CellDto>? Cells { get; set; }
        public Dictionary<string, string>? TabTitles { get; set; }
    }
    private sealed record LegacyDto(string Key, string? Label, string? Path, string? Args);
}
