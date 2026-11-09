using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Wormholes;
using AresToys.App.Views;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → Wormholes tab. Owns the list of <see cref="WormholeRowViewModel"/>
/// rendered as a grid, plus the "New wormhole" command that pops the same dialog the tray entry
/// uses. <see cref="ReloadAsync"/> rehydrates from <see cref="IWormholeRowSource"/> — called on tab
/// activation (mirrors <c>UploadersViewModel.ReloadAsync</c> / <c>HotkeysViewModel.ReloadAsync</c>
/// pattern) so the grid doesn't pay any I/O cost until the user actually navigates there.</summary>
public sealed partial class WormholesViewModel : ObservableObject
{
    private readonly IWormholeRowSource _rows;
    private readonly IWormholeWindowManager _manager;
    private bool _suppressDefaultsPersist;

    public ObservableCollection<WormholeRowViewModel> Rows { get; } = new();

    /// <summary>Layout presets sub-panel (save / restore / update / rename / delete named
    /// layout snapshots, and the preset auto-mapped to the current monitor setup).</summary>
    public WormholePresetsViewModel Presets { get; }

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Id of the wormhole the user last interacted with (clicked / selected an item /
    /// dragged). Drives the row-highlight in the Settings panel via <see cref="WormholeRowViewModel.IsSelected"/>.
    /// Null on first run (no interaction yet). Fed by <see cref="IWormholeWindowManager.WormholeFocused"/>;
    /// resets to null when a row is removed or on reload.</summary>
    [ObservableProperty] private Guid? _selectedWormholeId;

    /// <summary>App-wide default icon-tile size. 0 means "use the user's Windows desktop icon
    /// size" (see <see cref="DesktopIconSize"/>). Bound to the numeric input in Settings →
    /// Wormholes; mutating the setter persists to <see cref="WormholeDefaultsService"/> which
    /// then notifies every open wormhole to refresh.</summary>
    [ObservableProperty] private int _defaultIconSizePx;

    /// <summary>App-wide default opacity expressed as a percentage 30–100 (rendering in WPF
    /// uses 0.30–1.00). Bound to the slider in Settings → Wormholes; the underlying service
    /// holds the double form.</summary>
    [ObservableProperty] private int _defaultOpacityPercent = 70;

    /// <summary>App-wide opacity for the OuterFrame's 1 px accent ring + the drop shadow.
    /// Independent from <see cref="DefaultOpacityPercent"/> (which fades the body/header backdrops).</summary>
    [ObservableProperty] private int _defaultBorderOpacityPercent = 100;

    /// <summary>App-wide default tile padding (extra pixels around the icon inside its tile).
    /// Smaller = denser grid; larger = airier. Bound to a slider in Settings → Wormholes.</summary>
    [ObservableProperty] private int _defaultTilePaddingPx = 4;

    /// <summary>App-wide line spacing applied as a "negative-margin"–like effect: applied as the
    /// tile's bottom Margin so adjacent tile rows OVERLAP (negative) or get extra gap (positive).
    /// Does NOT change the text area / number of label lines (those live on <see cref="DefaultLabelFontSizePx"/>
    /// and <see cref="DefaultLabelMaxLines"/>).</summary>
    [ObservableProperty] private int _defaultLineSpacingPx = -4;

    /// <summary>App-wide label font size (px). Drives the FontSize of the TextBlock under each
    /// icon AND, via the line-height heuristic, the TileHeight reserved for the label area.</summary>
    [ObservableProperty] private int _defaultLabelFontSizePx = 12;

    /// <summary>Max number of wrapped lines a label may use before being ellipsized. 1 = 1-line
    /// Explorer style, 2 = default (wrap up to 2 lines), 3 = generous wrap.</summary>
    [ObservableProperty] private int _defaultLabelMaxLines = 2;

    /// <summary>When true the wormhole auto-disables Topmost on every launch gesture
    /// (double-click / Enter / open folder / drop-on-executable). Lets the user pull every
    /// wormhole above a fullscreen app via the toggle hotkey, click an icon to launch, and
    /// have the wormholes drop behind the launched app without a second hotkey press.</summary>
    [ObservableProperty] private bool _autoDisableTopmostOnLaunch;

    /// <summary>When true, web links (<c>.url</c>) in wormholes show the real site favicon
    /// (fetched on demand and stamped into the <c>.url</c> so Explorer shows it too). Default on.</summary>
    [ObservableProperty] private bool _webLinkFaviconsEnabled = true;

    /// <summary>When true (default), "Show desktop" — Win+D, Win+M, the taskbar's far-corner
    /// button — leaves the wormholes on screen instead of minimising them with everything else.</summary>
    [ObservableProperty] private bool _keepVisibleOnShowDesktop = true;

    /// <summary>When true, hovering a collapsed wormhole expands it for as long as the pointer
    /// stays on it. Visual only: the wormhole stays collapsed in its record.</summary>
    [ObservableProperty] private bool _expandCollapsedOnHover;

    /// <summary>When true a single click opens a tile and the tiles carry the hand cursor. Off by
    /// default: opening stays a double click, and the tiles show the arrow rather than a hand that
    /// promises a click will do something.</summary>
    [ObservableProperty] private bool _openWithOneClick;

    /// <summary>When true (default) desktop.ini, Thumbs.db and the other OS bookkeeping files are
    /// kept out of the tiles.</summary>
    [ObservableProperty] private bool _hideServiceFiles = true;

    /// <summary>When true (default) shortcut tiles (.lnk and .url alike) carry Explorer's little
    /// arrow in their bottom-left corner.</summary>
    [ObservableProperty] private bool _shortcutArrowOverlay = true;

    /// <summary>When true (default false) tile labels drop the file extension; renaming a tile
    /// still shows the full file name.</summary>
    [ObservableProperty] private bool _hideFileExtensions;

    /// <summary>Snap drag / resize to a <see cref="SnapGridSizePx"/> lattice anchored to the
    /// monitor's work area.</summary>
    [ObservableProperty] private bool _snapToGrid;

    /// <summary>Grid step in pixels used by <see cref="SnapToGrid"/>.</summary>
    [ObservableProperty] private int _snapGridSizePx = 16;

    /// <summary>Snap drag / resize to the edges and centre lines of the other open wormholes.</summary>
    [ObservableProperty] private bool _snapToWormholes;

    /// <summary>Snap drag / resize to the work area of the monitor the wormhole is on.</summary>
    [ObservableProperty] private bool _snapToScreenEdges;

    /// <summary>Pixels always left between a snapped wormhole and its target (another wormhole or
    /// the screen edge). 0 = flush.</summary>
    [ObservableProperty] private int _snapGapPx;

    public WormholesViewModel(IWormholeRowSource rows, IWormholeWindowManager manager)
    {
        _rows = rows;
        _manager = manager;
        Presets = new WormholePresetsViewModel(manager);
        // Hydrate the VM properties from the proxy's cached defaults (the satellite owns the real values).
        // Subsequent slider drags flow OUT through the partial-method setters below.
        ApplyDefaults(_rows.Defaults);
        // Live grid refresh: when the manager persists a record (user drag/resize on the live
        // chrome, lock toggle from chrome, hamburger rename, etc.), the matching row updates
        // its displayed fields in place. The event fires on the UI dispatcher already.
        _manager.RecordChanged += OnManagerRecordChanged;
        _manager.RecordDeleted += OnManagerRecordDeleted;
        _manager.WormholeFocused += OnManagerWormholeFocused;
    }

    /// <summary>The manager fires this when the user clicks on a wormhole (chrome or item). We
    /// propagate to <see cref="SelectedWormholeId"/> and refresh every row's
    /// <see cref="WormholeRowViewModel.IsSelected"/> so exactly one row reads as selected.</summary>
    private void OnManagerWormholeFocused(object? sender, Guid id)
    {
        if (SelectedWormholeId == id) return;
        SelectedWormholeId = id;
        foreach (var row in Rows) row.RefreshIsSelected();
    }

    /// <summary>Copies the cached defaults into the observable properties without echoing them back to the satellite.</summary>
    private void ApplyDefaults(WormholeWire.DefaultsDto d)
    {
        _suppressDefaultsPersist = true;
        try
        {
            DefaultIconSizePx = d.IconSizePx;
            DefaultOpacityPercent = (int)Math.Round(d.Opacity * 100);
            DefaultBorderOpacityPercent = (int)Math.Round(d.BorderOpacity * 100);
            DefaultTilePaddingPx = d.TilePaddingPx;
            DefaultLineSpacingPx = d.LineSpacingPx;
            DefaultLabelFontSizePx = d.LabelFontSizePx;
            DefaultLabelMaxLines = d.LabelMaxLines;
            AutoDisableTopmostOnLaunch = d.AutoDisableTopmostOnLaunch;
            WebLinkFaviconsEnabled = d.WebLinkFavicons;
            KeepVisibleOnShowDesktop = d.KeepVisibleOnShowDesktop;
            ExpandCollapsedOnHover = d.ExpandCollapsedOnHover;
            OpenWithOneClick = d.OpenWithOneClick;
            HideServiceFiles = d.HideServiceFiles;
            ShortcutArrowOverlay = d.ShortcutArrowOverlay;
            HideFileExtensions = d.HideFileExtensions;
            SnapToGrid = d.SnapToGrid;
            SnapGridSizePx = d.SnapGridSizePx;
            SnapToWormholes = d.SnapToWormholes;
            SnapToScreenEdges = d.SnapToScreenEdges;
            SnapGapPx = d.SnapGapPx;
        }
        finally { _suppressDefaultsPersist = false; }
    }

    /// <summary>Sends one default to the satellite (the only writer), which clamps and saves it.</summary>
    private void PushDefault(string key, string value)
        => _ = _rows.SetDefaultAsync(key, value, CancellationToken.None);

    private void PushDefault(string key, int value)
        => PushDefault(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void PushDefault(string key, double value)
        => PushDefault(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private void PushDefault(string key, bool value) => PushDefault(key, value ? "true" : "false");

    partial void OnDefaultIconSizePxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.IconSizeKey, value);
    }

    partial void OnDefaultOpacityPercentChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.OpacityKey, value / 100.0);
    }

    partial void OnDefaultBorderOpacityPercentChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.BorderOpacityKey, value / 100.0);
    }

    partial void OnDefaultTilePaddingPxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.TilePaddingKey, value);
    }

    partial void OnDefaultLineSpacingPxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.LineSpacingKey, value);
    }

    partial void OnDefaultLabelFontSizePxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.LabelFontSizeKey, value);
    }

    partial void OnDefaultLabelMaxLinesChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.LabelMaxLinesKey, value);
    }

    partial void OnWebLinkFaviconsEnabledChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.WebLinkFaviconsKey, value);
    }

    partial void OnAutoDisableTopmostOnLaunchChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.AutoDisableTopmostOnLaunchKey, value);
    }

    partial void OnKeepVisibleOnShowDesktopChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.KeepVisibleOnShowDesktopKey, value);
    }

    partial void OnExpandCollapsedOnHoverChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.ExpandCollapsedOnHoverKey, value);
    }

    partial void OnOpenWithOneClickChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.OpenWithOneClickKey, value);
    }

    partial void OnShortcutArrowOverlayChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.ShortcutArrowOverlayKey, value);
    }

    partial void OnHideFileExtensionsChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.HideFileExtensionsKey, value);
    }

    partial void OnHideServiceFilesChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.HideServiceFilesKey, value);
    }

    partial void OnSnapToGridChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.SnapToGridKey, value);
    }

    partial void OnSnapGridSizePxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.SnapGridSizeKey, value);
    }

    partial void OnSnapToWormholesChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.SnapToWormholesKey, value);
    }

    partial void OnSnapToScreenEdgesChanged(bool value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.SnapToScreenEdgesKey, value);
    }

    partial void OnSnapGapPxChanged(int value)
    {
        if (!_suppressDefaultsPersist) PushDefault(WormholeDefaultsService.SnapGapKey, value);
    }

    private void OnManagerRecordChanged(object? sender, Guid id)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        row?.RefreshDisplay();
    }

    /// <summary>A wormhole was deleted somewhere else — most often from its own chrome menu, while
    /// this panel sits open behind it. Drop the row so the grid doesn't advertise something that
    /// no longer exists. Unknown ids are ignored, and a row this panel already removed itself
    /// (its own Delete button) simply isn't found.</summary>
    private void OnManagerRecordDeleted(object? sender, Guid id)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row is null) return;
        Remove(row);
    }

    /// <summary>Pull the latest snapshot from the store and rebuild the rows. Idempotent —
    /// safe to call on every tab activation. Doesn't subscribe to store change events for v1
    /// (drag-induced LocationChanged would otherwise spam the grid with rebuilds); the user
    /// can re-click the sidebar entry to refresh after manipulating wormholes from chrome.
    ///
    /// Re-hydrates the default observables from the satellite's defaults too: this
    /// VM is built eagerly during DI (because <see cref="SettingsViewModel"/> takes it as a ctor
    /// dependency) BEFORE the async <c>LoadAsync</c> reads the persisted defaults from disk.
    /// The ctor's hydration therefore captures fallback values (95 % opacity etc.); re-hydrating
    /// here on every tab activation makes the panel always reflect what's actually on disk.</summary>
    public async Task ReloadAsync()
    {
        ApplyDefaults(await _rows.LoadDefaultsAsync(CancellationToken.None).ConfigureAwait(true));

        var records = await _rows.ListAsync(CancellationToken.None).ConfigureAwait(true);
        Rows.Clear();
        foreach (var r in records)
            Rows.Add(new WormholeRowViewModel(r, _rows, _manager, this));
        IsEmpty = Rows.Count == 0;

        await Presets.RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Drop a row from the grid, keeping <see cref="IsEmpty"/> honest. Called by a row's
    /// own Delete command and by <see cref="OnManagerRecordDeleted"/>; idempotent, so the two
    /// arriving for the same deletion is harmless. Avoids a full <see cref="ReloadAsync"/>
    /// round-trip.</summary>
    internal void Remove(WormholeRowViewModel row)
    {
        Rows.Remove(row);
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>Mirror a successful <see cref="IWormholeRowSource.MoveAsync"/> result on the visible
    /// <see cref="Rows"/> collection: yank the row and re-insert at <paramref name="newIndex"/>.
    /// Called by <see cref="WormholeRowViewModel"/>'s MoveUp/MoveDown commands after the store
    /// has already persisted the new order — keeps the grid responsive without a full Reload.</summary>
    internal void MoveRow(WormholeRowViewModel row, int newIndex)
    {
        var oldIndex = Rows.IndexOf(row);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Rows.Move(oldIndex, newIndex);
    }

    [RelayCommand]
    private async Task NewWormholeAsync()
    {
        var dlg = new NewWormholeDialog();
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var choice = dlg.Result;
        try
        {
            await _manager.CreateAsync(choice.Title, choice.SourceFolder, CancellationToken.None)
                .ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Couldn't create the wormhole:\n" + ex.Message,
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    // ── Source folder maintenance ────────────────────────────────────────────────────────

    /// <summary>How long the watchers stay released before re-attaching on their own. Long enough
    /// to rename a folder or drag it somewhere else in Explorer; short enough that a user who
    /// forgets about it doesn't lose live refresh for the rest of the session.</summary>
    private static readonly TimeSpan UnlockDuration = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _unlockTimer;
    private DateTime _unlockEndsAt;

    /// <summary>True while the folder watchers are released, i.e. while source folders can be
    /// renamed or moved from Explorer.</summary>
    [ObservableProperty] private bool _foldersUnlocked;

    /// <summary>Countdown label shown next to the unlock button ("1:42 left"), empty when the
    /// watchers are attached.</summary>
    [ObservableProperty] private string _foldersUnlockedCountdown = string.Empty;

    /// <summary>Release the folder watchers so Windows lets the user rename / move the folders
    /// the wormholes mirror, or re-attach them immediately if they're already released. A live
    /// watcher keeps a handle open inside the folder, and Windows refuses to rename any ancestor
    /// of a folder with an open handle in it.</summary>
    [RelayCommand]
    private void ToggleFolderUnlock()
    {
        if (_manager.WatchersPaused)
        {
            _manager.ResumeWatchers();
            StopUnlockCountdown();
            return;
        }

        _manager.PauseWatchers();
        FoldersUnlocked = true;
        _unlockEndsAt = DateTime.UtcNow + UnlockDuration;
        _unlockTimer ??= CreateUnlockTimer();
        UpdateUnlockCountdown();
        _unlockTimer.Start();
    }

    private DispatcherTimer CreateUnlockTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _unlockEndsAt)
            {
                _manager.ResumeWatchers();
                StopUnlockCountdown();
                return;
            }
            UpdateUnlockCountdown();
        };
        return timer;
    }

    private void UpdateUnlockCountdown()
    {
        var left = _unlockEndsAt - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        FoldersUnlockedCountdown = $"{(int)left.TotalMinutes}:{left.Seconds:D2}";
    }

    private void StopUnlockCountdown()
    {
        _unlockTimer?.Stop();
        FoldersUnlocked = false;
        FoldersUnlockedCountdown = string.Empty;
    }

    /// <summary>Repair every wormhole whose source folder no longer resolves, by pointing at the
    /// folder that now holds them. Each missing path is re-rooted under the chosen folder and only
    /// applied when the resulting path actually exists, so a wrong pick changes nothing.</summary>
    [RelayCommand]
    private async Task RelinkMissingSourcesAsync()
    {
        var missing = await _manager.MissingSourcesAsync(CancellationToken.None).ConfigureAwait(true);
        if (missing.Count == 0)
        {
            System.Windows.MessageBox.Show(
                AresToys.App.Resources.Strings.Wormhole_RelinkNoneMissing,
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = AresToys.App.Resources.Strings.Wormhole_RelinkPickFolderTitle,
        };
        if (dlg.ShowDialog() != true) return;

        var repaired = await _manager.RelinkMissingSourcesAsync(dlg.FolderName, CancellationToken.None)
            .ConfigureAwait(true);

        System.Windows.MessageBox.Show(
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                repaired > 0 ? AresToys.App.Resources.Strings.Wormhole_RelinkDone
                             : AresToys.App.Resources.Strings.Wormhole_RelinkNothingMatched,
                repaired, missing.Count),
            "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        await ReloadAsync().ConfigureAwait(true);
    }
}
