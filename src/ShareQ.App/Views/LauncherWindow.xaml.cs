using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using ShareQ.App.Services.Launcher;

namespace ShareQ.App.Views;

/// <summary>Top-most launcher overlay. Layout (top → bottom):
/// global F1-F10 strip, 10-tab strip, three QWERTY rows of 30 cells. Pressing F1-F10 fires
/// the global cell; pressing 1-9 or 0 swaps the active tab; pressing a QWERTY/punctuation
/// key fires the active tab's cell. Esc / focus loss closes. Right-click on a cell opens an
/// edit dialog; right-click on a tab header lets the user rename it.</summary>
public partial class LauncherWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly LauncherStore _store;
    private readonly IconService _icons;
    private readonly ILogger<LauncherWindow> _logger;

    /// <summary>Cached singleton instance. The launcher is registered AddSingleton so the open
    /// task can resolve it once and keep showing/hiding the same window — no visual-tree
    /// rebuild on every shortcut press. Set in the ctor, never cleared (singleton lifetime).</summary>
    private static LauncherWindow? _current;
    public static bool IsOpen => _current is { IsLoaded: true, IsVisible: true };
    public static void RequestClose() => _current?.BeginHide();

    private LauncherState _state = new(new Dictionary<string, LauncherCell>(),
                                        new Dictionary<string, string>());
    private string _activeTab = "1";
    /// <summary>True while a modal dialog (edit cell / rename tab) is open. The Deactivated
    /// handler reads this and skips its auto-close — without the gate the launcher would slam
    /// shut as soon as focus moved into the dialog. We can't just Hide() the launcher because
    /// ShowDialog with a hidden owner crashes WPF (the cause of the empty-cell click crash).</summary>
    private bool _suppressDeactivation;
    /// <summary>Latches once Hide() is queued / underway. Stops OnDeactivated from re-entering
    /// Hide on its own (the launched process steals focus → Deactivated fires → would call
    /// Hide on a window that's mid-teardown).</summary>
    private bool _isClosing;
    /// <summary>True while the user is in "drag-and-drop mode": the launcher stays open on
    /// deactivation, cells accept dropped files / folders / shortcuts, and a banner explains
    /// the new behaviour. Toggled via the chrome button or Esc (which exits drag mode instead
    /// of closing while the mode is on).</summary>
    private bool _dragMode;
    /// <summary>Internal clipboard for the per-cell Copy/Paste menu. Static so it survives
    /// re-opening the launcher within the same app session — a copy + restart is rare; a
    /// copy + close + reopen to paste somewhere else is common. Holds the source cell's
    /// payload (label/path/args/icon/etc.); only the TabKey/KeyChar of the destination get
    /// substituted on paste so the same data lands in a different slot.</summary>
    private static LauncherCell? _cellClipboard;
    /// <summary>Mouse-down anchor + cell key for the in-launcher drag-to-swap gesture. Set in
    /// PreviewMouseDown, consumed in PreviewMouseMove once the mouse has travelled past the
    /// system drag threshold. Cleared on mouse-up / drag completion.</summary>
    private System.Windows.Point? _cellDragStart;
    private string? _cellDragSourceKey;
    private const string CellDragFormat = "shareq.launcher.cell";
    /// <summary>Current search filter — case-insensitive substring matched against each cell's
    /// label and path. Empty string = no filter (everything visible). Function-row cells are
    /// always visible regardless of this value.</summary>
    private string _filter = string.Empty;

    private readonly ObservableCollection<CellViewModel> _functionRow = [];
    private readonly ObservableCollection<TabHeaderViewModel> _tabHeaders = [];
    private readonly ObservableCollection<CellViewModel> _row1 = [];
    private readonly ObservableCollection<CellViewModel> _row2 = [];
    private readonly ObservableCollection<CellViewModel> _row3 = [];

    public LauncherWindow(LauncherStore store, IconService icons, ILogger<LauncherWindow> logger)
    {
        InitializeComponent();
        ShareQ.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        ShareQ.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        _store = store;
        _icons = icons;
        _logger = logger;
        FunctionRow.ItemsSource = _functionRow;
        TabStrip.ItemsSource    = _tabHeaders;
        Row1Host.ItemsSource    = _row1;
        Row2Host.ItemsSource    = _row2;
        Row3Host.ItemsSource    = _row3;

        // Cache the singleton instance for the toggle-close path.
        _current = this;

        // Restore the saved geometry synchronously in the ctor so the first paint already
        // lands at the right size/position — async-loading after Loaded would briefly flash
        // the default 1280×640 centred. SQLite read is ~1ms, fine to block.
        TryRestoreGeometry();

        // Re-do "fresh open" work every time the window becomes visible, not just on first
        // Loaded. Singleton lifetime means Loaded fires once per app run, but we want the
        // user's edits (cell additions / drag-mode toggles from a previous open) reflected
        // every time they hit the shortcut.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true)
            {
                // Hiding then re-showing resets _isClosing so the next deactivate can re-trigger.
                _isClosing = false;
                await ReloadAsync();
                // Drag-mode persistence: explicit open-in-drag from the host (StartInDragMode)
                // wins; otherwise the persisted flag from the previous close is honoured. So
                // closing while in drag mode reopens in drag mode automatically.
                var startDrag = StartInDragMode || await _store.LoadDragModeAsync(CancellationToken.None);
                SetDragMode(startDrag);
                StartInDragMode = false;   // consume the explicit flag so a normal re-open isn't sticky
                // Focus the launcher root, NOT the search box — auto-focusing the search swallows
                // every shortcut key (Q, F1, …) the user wanted to press to fire a cell. Ctrl+F
                // gives them an explicit way into the search box when they actually want to type.
                Focus();
            }
            else
            {
                // Persist geometry on every hide. Closing event no longer fires (the window
                // never closes during the app session) so this is the moment to snapshot.
                PersistGeometry();
            }
        };
    }

    private void TryRestoreGeometry()
    {
        LauncherGeometry? geom;
        try { geom = _store.LoadGeometryAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { geom = null; }
        if (geom is null) return;

        Width  = Math.Max(MinWidth,  geom.Width);
        Height = Math.Max(MinHeight, geom.Height);

        // Only restore the position if it falls inside the current virtual screen rectangle —
        // otherwise the user might disconnect a monitor between sessions and the launcher
        // would open completely off-screen. Falls back to CenterScreen in that case.
        if (IsOnVirtualScreen(geom.Left, geom.Top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = geom.Left;
            Top  = geom.Top;
        }
    }

    private static bool IsOnVirtualScreen(double left, double top, double w, double h)
    {
        // Anchor: at least the top-left 40×40 region must overlap any connected monitor's work
        // area. Cheaper than full geometry intersection and good enough — if that corner is
        // visible the user can grab the title bar and drag the rest into view.
        var rect = new System.Windows.Rect(left, top, Math.Min(w, 40), Math.Min(h, 40));
        var virt = new System.Windows.Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virt.IntersectsWith(rect);
    }

    /// <summary>Persist size + position before the user dismisses the launcher, AND intercept
    /// the X-button / Alt+F4 close so the singleton instance survives. Without the cancel
    /// the FluentWindow titlebar's close button would tear down the window and the next
    /// Win+Z press would throw "Cannot Show after a Window has closed". App shutdown
    /// bypasses the cancel via <see cref="App.IsShuttingDown"/>.</summary>
    private void OnLauncherClosing(object sender, CancelEventArgs e)
    {
        PersistGeometry();
        if (App.IsShuttingDown) return;
        e.Cancel = true;
        BeginHide();
    }

    /// <summary>Snapshot the window's current size + on-screen position to the store.
    /// Called from IsVisibleChanged on hide and from the Closing event on app shutdown — both
    /// are the moments where we still have valid layout numbers but the user is "done" with
    /// the window for now. RestoreBounds when maximised so we never persist a full-screen
    /// rectangle as the user-set size.</summary>
    private void PersistGeometry()
    {
        var bounds = WindowState == WindowState.Normal
            ? new System.Windows.Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        var geom = new LauncherGeometry(bounds.Width, bounds.Height, bounds.Left, bounds.Top);
        try { _store.SaveGeometryAsync(geom, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "LauncherWindow: failed to persist geometry"); }
    }


    /// <summary>Set by the host (e.g. <c>OpenLauncherDragModeTask</c>) before <c>Show()</c> to
    /// open the launcher already in drag-and-drop mode. Read once on Loaded.</summary>
    public bool StartInDragMode { get; set; }

    private async Task ReloadAsync()
    {
        _state = await _store.LoadAsync(CancellationToken.None);
        // Restore the active tab from settings on first reload (StorageIndex of the previous
        // open session) so the user lands back where they left off. Subsequent reloads (after
        // a cell edit / rename) keep whatever tab they're currently viewing.
        if (!_activeTabRestored)
        {
            var saved = await _store.LoadActiveTabAsync(CancellationToken.None);
            if (saved is not null) _activeTab = saved;
            _activeTabRestored = true;
        }
        RebuildFunctionRow();
        RebuildTabHeaders();
        RebuildActiveTab();
    }
    private bool _activeTabRestored;

    private void RebuildFunctionRow()
    {
        _functionRow.Clear();
        foreach (var k in LauncherKeyboardLayout.FunctionKeys)
        {
            var cell = _state.Get(LauncherTabs.FunctionStrip, k);
            // F-row is exempt from the filter on purpose — those keys are global "always-on
            // shortcuts" and the user wants them reachable while searching for tab cells.
            _functionRow.Add(new CellViewModel(cell, _icons.GetIcon(cell.IconPath, cell.Path, cell.IconIndex), matchesFilter: true));
        }
    }

    private void RebuildTabHeaders()
    {
        _tabHeaders.Clear();
        foreach (var t in LauncherKeyboardLayout.TabKeys)
        {
            // Tab title hides when the active filter rules out every configured cell on this
            // tab — visually marks dead tabs without removing them from navigation. With no
            // filter active, every tab is treated as having matches and titles all show.
            var hasMatches = string.IsNullOrEmpty(_filter)
                || LauncherKeyboardLayout.AllTabKeyChars()
                    .Any(k => CellMatchesFilter(_state.Get(t, k)));
            _tabHeaders.Add(new TabHeaderViewModel(t, _state.TabTitle(t),
                isActive: t == _activeTab, hasFilteredMatches: hasMatches));
        }
    }

    private void RebuildActiveTab()
    {
        FillRow(_row1, LauncherKeyboardLayout.Row1);
        FillRow(_row2, LauncherKeyboardLayout.Row2);
        FillRow(_row3, LauncherKeyboardLayout.Row3);

        void FillRow(ObservableCollection<CellViewModel> dst, IReadOnlyList<string> keys)
        {
            dst.Clear();
            foreach (var k in keys)
            {
                var cell = _state.Get(_activeTab, k);
                dst.Add(new CellViewModel(cell, _icons.GetIcon(cell.IconPath, cell.Path, cell.IconIndex),
                    matchesFilter: CellMatchesFilter(cell)));
            }
        }
    }

    /// <summary>True if a cell should be visible under the current filter. No filter ⇒ every
    /// configured cell visible (empty cells stay visible too — they're the "drop here" slots).
    /// With a filter set, empty cells hide so the search reads as "show me what matches".</summary>
    private bool CellMatchesFilter(LauncherCell cell)
    {
        if (string.IsNullOrEmpty(_filter)) return true;
        if (!cell.IsConfigured) return false;
        return cell.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || cell.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filter = SearchBox.Text?.Trim() ?? string.Empty;
        // If the active tab is now empty under the new filter, jump to the first tab that has
        // matches. Single-match-anywhere case: the user types something specific, sees the
        // tab they care about even if they were looking elsewhere when they started typing.
        if (!string.IsNullOrEmpty(_filter) && !TabHasMatches(_activeTab))
        {
            var jumpTo = LauncherKeyboardLayout.TabKeys.FirstOrDefault(TabHasMatches);
            if (jumpTo is not null) _activeTab = jumpTo;
        }
        // Cheap: each rebuild creates ~30 view-model instances. No virtualization concerns and
        // the Get/match calls are O(1) per cell. Re-rendering on every keystroke keeps the UX
        // snappy (the user types "ph" → instantly see only Photoshop / Phaser / etc).
        RebuildTabHeaders();
        RebuildActiveTab();
    }

    private bool TabHasMatches(string tabKey) =>
        LauncherKeyboardLayout.AllTabKeyChars()
            .Any(k => CellMatchesFilter(_state.Get(tabKey, k)));

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Click-outside / alt-tab closes the launcher. Matches MaxLaunchpad's ephemeral overlay
        // behaviour. Modal dialogs flip _suppressDeactivation so opening the dialog doesn't
        // immediately close us when focus moves out. _isClosing guards against re-entry when
        // FireCell already kicked off a Close. In drag mode the launcher is intentionally
        // sticky — focus moves to Explorer or another window while the user picks files, and
        // the launcher must not slam shut behind their back.
        if (_suppressDeactivation || _isClosing || _dragMode) return;
        BeginHide();
    }

    /// <summary>Hide on the next dispatcher cycle so the current event handler (mouse / key /
    /// deactivation) can fully unwind before the visual state changes. Hide() (not Close())
    /// because the launcher is a singleton: the next Show is supposed to re-use the same
    /// instance — that's what makes the shortcut snappy. State that needs refreshing on the
    /// next show happens in IsVisibleChanged.</summary>
    private void BeginHide()
    {
        if (_isClosing) return;
        _isClosing = true;
        Dispatcher.BeginInvoke(new Action(Hide), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F gives focus to the search box. Anywhere — even from an empty cell or while
        // already in another text field. Mirrors the conventional "find" shortcut so users
        // know how to reach the filter without us hijacking focus on launcher open.
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Esc has three roles: clear an active search first (so the user can bail on a
            // filter without dismissing the panel), then exit drag mode if it's on, then close.
            // Layered the way the user would mentally back out — undo the most-recent thing first.
            if (!string.IsNullOrEmpty(_filter)) { SearchBox.Text = string.Empty; return; }
            if (_dragMode) SetDragMode(false);
            else BeginHide();
            return;
        }

        var inSearchBox = Keyboard.FocusedElement is System.Windows.Controls.TextBox;

        // Arrow keys nudge through the tab strip. Outside the search box: all four arrows
        // (left/right/up/down). Inside the search box: only up/down — left/right belong to
        // the textbox caret so the user can edit their query without losing the navigation
        // affordance. Wraps around at the ends so 0 → 1 and 1 → 0 both work.
        var horizontal = e.Key is Key.Left or Key.Right;
        var vertical = e.Key is Key.Up or Key.Down;
        if (vertical || (horizontal && !inSearchBox))
        {
            var tabs = LauncherKeyboardLayout.TabKeys;
            var idx = 0;
            for (var i = 0; i < tabs.Count; i++) if (tabs[i] == _activeTab) { idx = i; break; }
            var delta = e.Key is Key.Right or Key.Down ? 1 : -1;
            var next = (idx + delta + tabs.Count) % tabs.Count;
            SwitchTab(tabs[next]);
            e.Handled = true;
            return;
        }

        // While the search box has focus, every other key belongs to the user's typing — we
        // must not interpret them as fire/switch shortcuts or they'd type 'Q' and accidentally
        // launch the Q cell. Esc + arrows are handled above so the user keeps Esc / tab nav.
        if (inSearchBox) return;

        // F1..F10 fire global cells regardless of selected tab.
        var fkey = e.Key switch
        {
            Key.F1 => "F1",  Key.F2 => "F2",  Key.F3 => "F3",  Key.F4 => "F4",  Key.F5 => "F5",
            Key.F6 => "F6",  Key.F7 => "F7",  Key.F8 => "F8",  Key.F9 => "F9",  Key.F10 => "F10",
            _ => null,
        };
        if (fkey is not null)
        {
            HandleKey(LauncherTabs.FunctionStrip, fkey);
            return;
        }

        // Number row 1..0 switches active tab. Use the top-row keys (D1..D0) and the numpad.
        var tabKey = e.Key switch
        {
            Key.D1 or Key.NumPad1 => "1", Key.D2 or Key.NumPad2 => "2", Key.D3 or Key.NumPad3 => "3",
            Key.D4 or Key.NumPad4 => "4", Key.D5 or Key.NumPad5 => "5", Key.D6 or Key.NumPad6 => "6",
            Key.D7 or Key.NumPad7 => "7", Key.D8 or Key.NumPad8 => "8", Key.D9 or Key.NumPad9 => "9",
            Key.D0 or Key.NumPad0 => "0",
            _ => null,
        };
        if (tabKey is not null)
        {
            SwitchTab(tabKey);
            return;
        }

        // Otherwise: fire the active tab's cell for the printable key.
        var ch = TabKeyChar(e.Key);
        if (ch is null) return;
        HandleKey(_activeTab, ch);
    }

    /// <summary>Common entry for "user pressed a key bound to a cell". Fires the cell when
    /// configured, otherwise just logs a debug breadcrumb (the user wanted to see which keys
    /// they pressed even when nothing happened, useful while assembling a tab). In drag mode
    /// firing is suppressed — the launcher is supposed to stay put and accept drops only.</summary>
    private void HandleKey(string tabKey, string keyChar)
    {
        var cell = _state.Get(tabKey, keyChar);
        if (_dragMode)
        {
            _logger.LogDebug("Launcher key {TabKey}:{KeyChar} pressed (drag mode — fire suppressed)", tabKey, keyChar);
            return;
        }
        if (cell.IsConfigured)
        {
            FireCell(cell);
            return;
        }
        _logger.LogDebug("Launcher key {TabKey}:{KeyChar} pressed — no mapping", tabKey, keyChar);
    }

    private void OnDragToggleClick(object sender, RoutedEventArgs e) => SetDragMode(!_dragMode);

    /// <summary>Switch the drag-and-drop mode flag and reflect it in the UI (banner + toggle
    /// button label). Cells already declare AllowDrop="True" in XAML; their Drop handler
    /// reads <see cref="_dragMode"/> and ignores drops outside the mode. Window drag and
    /// edge-resize are now handled natively by the FluentWindow titlebar, so the conditional
    /// chrome-drag and Thumb-visibility code that used to live here is gone — the user can
    /// move/resize the launcher anytime, drag mode just changes drop-target behaviour.</summary>
    private void SetDragMode(bool on)
    {
        _dragMode = on;
        DragModeBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DragToggle.Content = on ? "✓ Drag mode (on)" : "📥 Drag mode";
        // Persist so closing the launcher while in drag mode reopens it the same way next time.
        // Fire-and-forget: persistence is sub-ms (single SQLite key write).
        _ = _store.SaveDragModeAsync(on, CancellationToken.None);
    }

    private void OnCellDragEnter(object sender, DragEventArgs e)
    {
        // We only care about drags in drag mode. Two flavours: file/folder drag from Explorer
        // (DataFormats.FileDrop → map onto the cell) or an in-launcher drag from another cell
        // (CellDragFormat → swap the two cells' contents). Anything else: refuse the drop.
        if (!_dragMode)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var hasFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        var hasCell = e.Data.GetDataPresent(CellDragFormat);
        if (!hasFile && !hasCell)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = hasCell ? DragDropEffects.Move : DragDropEffects.Link;
        // Visual cue: brighten the drop target border so the user can tell which cell they're
        // about to map / swap with. Reverted in DragLeave / Drop.
        if (sender is Border b) b.BorderBrush = System.Windows.Media.Brushes.White;
        e.Handled = true;
    }

    private void OnCellDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = DefaultCellBorderBrush();
    }

    private void OnCellDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = DefaultCellBorderBrush();
        if (!_dragMode) return;
        if (sender is not Border border || border.Tag is not string composed) return;
        if (!_state.Cells.TryGetValue(composed, out var existing)) return;

        // Cell-on-cell drag: swap the two cells' payloads. The source's TabKey/KeyChar move
        // into the target slot and vice-versa, so the user effectively re-arranges the grid
        // by drag. Empty source key → no-op (drop on self or unknown source).
        if (e.Data.GetDataPresent(CellDragFormat))
        {
            if (e.Data.GetData(CellDragFormat) is not string sourceKey) return;
            if (string.Equals(sourceKey, composed, StringComparison.OrdinalIgnoreCase)) return;
            if (!_state.Cells.TryGetValue(sourceKey, out var source)) return;
            // Build the swapped pair: target's slot gets the source's content (re-keyed to
            // the target's TabKey/KeyChar), source's slot gets the existing target content
            // (re-keyed back). Empty cells flow naturally — pasting an empty into a slot
            // clears it.
            var newTarget = source  with { TabKey = existing.TabKey, KeyChar = existing.KeyChar };
            var newSource = existing with { TabKey = source.TabKey,  KeyChar = source.KeyChar };
            _logger.LogInformation("Launcher: swapped cell {Source} ↔ {Target}",
                source.ComposedKey, existing.ComposedKey);
            _ = SwapCellsAsync(newSource, newTarget);
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var dropped = paths[0];   // first item only — multi-file drop on a single cell would be ambiguous
        var label = TryDeriveLabel(dropped);
        var updated = new LauncherCell(existing.TabKey, existing.KeyChar, label, dropped, string.Empty);
        _logger.LogInformation("Launcher: dropped {Path} onto {Key} → label '{Label}'",
            dropped, existing.ComposedKey, label);
        _ = PersistCellAndReloadAsync(updated);
        e.Handled = true;
    }

    /// <summary>Persist a pair of swapped cells in one go. Two sequential UpdateCellAsync
    /// writes are fine: the store rewrites the whole blob on each call so there's no risk
    /// of one half landing without the other (worst case: SaveAsync error after the first
    /// write leaves the cells "moved" rather than "swapped" — visible, recoverable).</summary>
    private async Task SwapCellsAsync(LauncherCell a, LauncherCell b)
    {
        try
        {
            await _store.UpdateCellAsync(a, CancellationToken.None);
            await _store.UpdateCellAsync(b, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherWindow: failed to swap cells {A} ↔ {B}",
                a.ComposedKey, b.ComposedKey);
        }
    }

    /// <summary>Resolve the cell-border brush from the live theme — used when the drop hover
    /// effect ends, and we want to put the border back the way the XAML's DynamicResource
    /// would have left it. Reads from Application.Resources so a re-themed app updates here
    /// for free; falls back to a hard-coded grey if for some reason the resource is absent.</summary>
    private static Brush DefaultCellBorderBrush()
        => Application.Current?.Resources["AccentBackgroundDarkBorderBrush"] as Brush
           ?? new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

    /// <summary>Pick a sensible label from a dropped path: filename without extension for
    /// files, the directory name for folders. The user can always rename via the cell edit
    /// dialog later if they want something different.</summary>
    private static string TryDeriveLabel(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) return new System.IO.DirectoryInfo(path).Name;
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void SwitchTab(string tabKey)
    {
        if (_activeTab == tabKey) return;
        _activeTab = tabKey;
        RebuildTabHeaders();
        RebuildActiveTab();
        // Persist the choice fire-and-forget so the next launcher open lands on this tab.
        // SQLite write is ~1ms; a stray failure isn't worth blocking the UI for.
        _ = _store.SaveActiveTabAsync(tabKey, CancellationToken.None);
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tabKey) SwitchTab(tabKey);
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string tabKey) return;
        var current = _state.TabTitle(tabKey);
        var dlg = new TabTitleDialog(tabKey, current) { Owner = this };
        _suppressDeactivation = true;
        try
        {
            if (dlg.ShowDialog() == true)
            {
                _ = PersistTitleAndReloadAsync(tabKey, dlg.TabTitle);
            }
        }
        finally
        {
            _suppressDeactivation = false;
            Activate();
        }
    }

    private async Task PersistTitleAndReloadAsync(string tabKey, string title)
    {
        try
        {
            await _store.UpdateTabTitleAsync(tabKey, title, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherWindow: failed to rename tab {TabKey}", tabKey);
        }
    }

    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string composed) return;
        if (!_state.Cells.TryGetValue(composed, out var cell)) return;
        // In drag mode, left-click is reserved for drag-to-swap (PreviewMouseDown / Move set
        // up the gesture). A click without movement is a no-op — we don't fire the cell (the
        // user is editing, not launching) and we don't auto-open the edit dialog (right-click
        // → Edit… is the way). Stops the launcher from launching apps when the user is
        // rearranging the grid.
        if (_dragMode) return;
        if (cell.IsConfigured) FireCell(cell);
        else EditCell(cell);   // empty cells: clicking is "configure me"
    }

    // OnCellRightClick was here — replaced by the per-cell ContextMenu wired up in XAML so
    // the user gets a proper Open / Edit / Copy / Paste / Delete menu instead of jumping
    // straight into the edit dialog.

    /// <summary>Look up the cell underlying a ContextMenu item click. The menu itself is a
    /// shared resource whose DataContext is rebound to the right cell's view-model via
    /// PlacementTarget binding (see Window.Resources), so each MenuItem.Click reads its
    /// DataContext to find which cell the user invoked the menu on.</summary>
    private LauncherCell? CellFromMenuItem(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem item) return null;
        if (item.DataContext is not CellViewModel vm) return null;
        return _state.Cells.TryGetValue(vm.ComposedKey, out var c) ? c : null;
    }

    private void OnMenuOpenLocation(object sender, RoutedEventArgs e)
    {
        var cell = CellFromMenuItem(sender);
        if (cell is null || !cell.IsConfigured) return;
        try
        {
            var path = Environment.ExpandEnvironmentVariables(cell.Path);
            if (Directory.Exists(path))
            {
                // Folder: open the folder itself.
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else
            {
                // File: open Explorer with the file pre-selected. /select wants quoted path.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherWindow: open-file-location failed for {Key}", cell.ComposedKey);
        }
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        var cell = CellFromMenuItem(sender);
        if (cell is not null) EditCell(cell);
    }

    private void OnMenuCopy(object sender, RoutedEventArgs e)
    {
        var cell = CellFromMenuItem(sender);
        if (cell is null || !cell.IsConfigured) return;
        _cellClipboard = cell;
        _logger.LogDebug("Launcher: copied cell {Key} to internal clipboard", cell.ComposedKey);
    }

    private void OnMenuPaste(object sender, RoutedEventArgs e)
    {
        var target = CellFromMenuItem(sender);
        if (target is null || _cellClipboard is null) return;
        // Preserve the destination's TabKey + KeyChar (a paste shouldn't move the data into
        // some other slot than the one the user picked). Everything else carries over.
        var pasted = _cellClipboard with { TabKey = target.TabKey, KeyChar = target.KeyChar };
        _ = PersistCellAndReloadAsync(pasted);
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        var cell = CellFromMenuItem(sender);
        if (cell is null) return;
        _ = PersistCellAndReloadAsync(LauncherCell.Empty(cell.TabKey, cell.KeyChar));
    }

    // ── Drag-to-swap inside the launcher ─────────────────────────────────────────────

    private void OnCellPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only arm the drag in drag mode + only on configured cells. Empty cells have nothing
        // to drag; outside drag mode the launcher is meant to be a one-shot key-fire panel.
        if (!_dragMode) { _cellDragStart = null; _cellDragSourceKey = null; return; }
        if (sender is not Border b || b.Tag is not string composed) return;
        if (!_state.Cells.TryGetValue(composed, out var cell) || !cell.IsConfigured) return;
        _cellDragStart = e.GetPosition(this);
        _cellDragSourceKey = composed;
    }

    private void OnCellPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_cellDragStart is null || _cellDragSourceKey is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _cellDragStart = null; _cellDragSourceKey = null; return; }
        var pos = e.GetPosition(this);
        var dx = Math.Abs(pos.X - _cellDragStart.Value.X);
        var dy = Math.Abs(pos.Y - _cellDragStart.Value.Y);
        // Wait for the system-defined drag threshold so a quick click doesn't masquerade as
        // a drag — picks up SystemParameters values that respect the user's mouse settings.
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        var sourceKey = _cellDragSourceKey;
        _cellDragStart = null;
        _cellDragSourceKey = null;
        if (sender is not Border b) return;
        var data = new DataObject(CellDragFormat, sourceKey);
        DragDrop.DoDragDrop(b, data, DragDropEffects.Move);
    }

    private void EditCell(LauncherCell cell)
    {
        var dlg = new LauncherCellEditDialog(cell, _icons) { Owner = this };
        // Don't Hide() the launcher: ShowDialog with a hidden owner crashes WPF (this was the
        // empty-cell click crash). Instead suppress our own auto-close-on-deactivate while the
        // dialog is up; the launcher stays visible behind it, gets the focus back on close.
        _suppressDeactivation = true;
        try
        {
            var ok = dlg.ShowDialog() == true && dlg.Result is not null;
            if (ok) { _ = PersistCellAndReloadAsync(dlg.Result!); }
        }
        finally
        {
            _suppressDeactivation = false;
            Activate();
        }
    }

    private async Task PersistCellAndReloadAsync(LauncherCell updated)
    {
        try
        {
            await _store.UpdateCellAsync(updated, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherWindow: failed to persist cell {Key}", updated.ComposedKey);
        }
    }

    private void FireCell(LauncherCell cell)
    {
        if (!cell.IsConfigured) return;
        try
        {
            // Activate-if-running: when the cell declares a window title or process name,
            // first try to focus an existing instance instead of spawning a new one. Same
            // semantic MaxLauncher exposes as "WindowTitle / ProcessName" cell fields.
            if (WindowActivator.TryActivate(cell.WindowTitle, cell.ProcessName))
            {
                _logger.LogInformation("LauncherWindow: activated existing window for {Key} (title='{Title}' proc='{Proc}')",
                    cell.ComposedKey, cell.WindowTitle, cell.ProcessName);
                BeginHide();
                return;
            }

            var path = Environment.ExpandEnvironmentVariables(cell.Path);
            var args = Environment.ExpandEnvironmentVariables(cell.Args ?? string.Empty);
            string workingDir = string.Empty;
            try { workingDir = Path.GetDirectoryName(path) ?? string.Empty; } catch { /* ignore */ }

            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = true,    // shell so .lnk / .bat / URL targets resolve, and so Verb=runas can prompt UAC
                WorkingDirectory = workingDir,
                WindowStyle = MapWindowMode(cell.WindowMode),
            };
            // "runas" verb triggers Windows' UAC prompt and starts the child elevated. Only
            // set when explicitly requested — the default (no verb) launches at the current
            // process's integrity level, which is what unprivileged apps want.
            if (cell.RunAsAdmin) psi.Verb = "runas";

            Process.Start(psi);
            _logger.LogInformation("LauncherWindow: fired {Key} → {Path} {Args} (admin={Admin}, mode={Mode})",
                cell.ComposedKey, path, args, cell.RunAsAdmin, cell.WindowMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LauncherWindow: failed to launch cell {Key} → {Path}",
                cell.ComposedKey, cell.Path);
        }
        BeginHide();   // one-shot menu: summon, fire, dismiss (deferred — see BeginClose).
    }

    private static ProcessWindowStyle MapWindowMode(LauncherWindowMode mode) => mode switch
    {
        LauncherWindowMode.Maximized => ProcessWindowStyle.Maximized,
        LauncherWindowMode.Minimized => ProcessWindowStyle.Minimized,
        LauncherWindowMode.Hidden    => ProcessWindowStyle.Hidden,
        _                            => ProcessWindowStyle.Normal,
    };

    /// <summary>Map a WPF Key for the QWERTY/punctuation cells (the 30 keys per tab). Returns
    /// null for everything else so the handler can no-op.</summary>
    private static string? TabKeyChar(Key k) => k switch
    {
        Key.Q => "Q", Key.W => "W", Key.E => "E", Key.R => "R", Key.T => "T",
        Key.Y => "Y", Key.U => "U", Key.I => "I", Key.O => "O", Key.P => "P",
        Key.A => "A", Key.S => "S", Key.D => "D", Key.F => "F", Key.G => "G",
        Key.H => "H", Key.J => "J", Key.K => "K", Key.L => "L", Key.OemSemicolon => ";",
        Key.Z => "Z", Key.X => "X", Key.C => "C", Key.V => "V", Key.B => "B",
        Key.N => "N", Key.M => "M", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
        _ => null,
    };

    /// <summary>Cell row / strip view-model. Wraps a <see cref="LauncherCell"/> + an optional
    /// pre-resolved icon so the DataTemplate can bind to both without doing the lookup itself.
    /// <see cref="CellVisibility"/> reflects the launcher's search filter — Hidden (not Collapsed)
    /// so the surrounding UniformGrid keeps its layout while individual cells fade out.</summary>
    private sealed class CellViewModel
    {
        public CellViewModel(LauncherCell cell, BitmapSource? icon, bool matchesFilter)
        {
            KeyChar = cell.KeyChar;
            Label = cell.Label;
            Path = cell.Path;
            ComposedKey = cell.ComposedKey;
            Icon = icon;
            CellVisibility = matchesFilter ? Visibility.Visible : Visibility.Hidden;
        }
        public string KeyChar { get; }
        public string Label { get; }
        public string Path { get; }
        public string ComposedKey { get; }
        public BitmapSource? Icon { get; }
        /// <summary>Drives ToolTipService.IsEnabled — false on empty cells so WPF doesn't pop
        /// a hollow rectangle when the user hovers an unmapped slot.</summary>
        public bool HasPath => !string.IsNullOrWhiteSpace(Path);
        /// <summary>Glyph shown on the cell — uses the user's keyboard layout so an Italian
        /// QWERTY shows "ò" / "-" instead of the canonical US ";" / "/". Storage stays US so
        /// KeyDown matching keeps working regardless of layout.</summary>
        public string DisplayKeyChar => KeyboardLayoutMapper.GetDisplayChar(KeyChar);
        /// <summary>Hidden when the cell doesn't match the launcher's current search filter.
        /// Hidden (not Collapsed) so the keyboard layout keeps its shape — matches MaxLauncher's
        /// behaviour where filtered-out keys leave their slot empty rather than re-flowing.</summary>
        public Visibility CellVisibility { get; }
    }

    /// <summary>Tab header view-model. Active tab paints with the user's main accent; inactive
    /// tabs sit on the dark accent so the strip reads as "everything in the same family but the
    /// active one pops". Brushes resolve from Application.Resources so the theme service's
    /// updates flow into the launcher without us touching anything when the user re-themes.</summary>
    private sealed class TabHeaderViewModel : INotifyPropertyChanged
    {
        public TabHeaderViewModel(string tabKey, string title, bool isActive, bool hasFilteredMatches)
        {
            TabKey = tabKey;
            Title = title;
            IsActive = isActive;
            // The whole header hides when no cell on this tab matches the active filter — the
            // user wanted dead tabs gone entirely, not just unlabelled. Hidden (not Collapsed)
            // so the UniformGrid keeps its 10 slots and the visible tabs don't reflow.
            HeaderVisibility = hasFilteredMatches ? Visibility.Visible : Visibility.Hidden;
        }
        public string TabKey { get; }
        public string Title { get; }
        public bool IsActive { get; }
        public Visibility HeaderVisibility { get; }
        public Brush HeaderBackground
        {
            get
            {
                var key = IsActive ? "AccentBackgroundBrush" : "AccentBackgroundDarkBrush";
                return Application.Current?.Resources[key] as Brush
                    ?? new SolidColorBrush(IsActive
                        ? Color.FromRgb(0x75, 0x1C, 0x8B)
                        : Color.FromRgb(0x37, 0x12, 0x42));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
