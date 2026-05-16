using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.Wormholes;

namespace AresToys.App.Views;

public partial class WormholeWindow : Window
{
    // Segoe Fluent Icons code points pinned via ConvertFromUtf32 so the source stays pure
    // ASCII (some tooling round-trips strip or re-encode private-use glyphs inline).
    private static readonly string ChevronUpGlyph   = char.ConvertFromUtf32(0xE70E);
    private static readonly string ChevronDownGlyph = char.ConvertFromUtf32(0xE70D);
    private static readonly string LockClosedGlyph  = char.ConvertFromUtf32(0xE72E);
    private static readonly string LockOpenGlyph    = char.ConvertFromUtf32(0xE785);
    // (Per-kind glyph removed when the Shortcuts/Portal distinction was unified into a single
    // "folder mirror" type — every wormhole is the same kind now, the chrome doesn't need to
    // disambiguate.)

    /// <summary>Hard cap on Portal items rendered per wormhole. Mirrors the spec §6.4 default.
    /// Beyond this we emit a one-shot toast-style banner and truncate — large folders block
    /// the dispatcher today (no virtualisation in MVP).</summary>
    private const int PortalItemCap = 500;

    private readonly WormholeRecord _record;
    private readonly Action _onPersist;
    private readonly IconService _icons;
    private readonly string _wormholesRoot;
    /// <summary>Shared defaults — icon size + opacity — read on every <see cref="EffectiveIconSize"/>
    /// + <see cref="ApplyAppearance"/> call. Null when the manager didn't wire one (older tests
    /// / direct construction); in that case we fall through to <see cref="DesktopIconSize"/>
    /// for icon size and the legacy 0.95 hardcoded opacity.</summary>
    private readonly Services.Wormholes.WormholeDefaultsService? _defaults;
    /// <summary>Manager used to fan-out "I just got a selection — clear yours" events to sibling
    /// wormholes. Null only on direct test construction; in normal app flow this is wired by
    /// <see cref="Services.Wormholes.WormholeWindowManager"/> at spawn time.</summary>
    private readonly Services.Wormholes.IWormholeWindowManager? _manager;
    private readonly ObservableCollection<WormholeItemViewModel> _items = new();
    private bool _isClosingFromManager;
    private bool _portalItemCapReached;

    /// <summary>Paths currently in the "cut" state — tile shown at 50 % opacity. Tracked
    /// separately from the VM list because the VMs are recreated on every refresh tick (so
    /// the IsCutMarked flag would lose its setting on the next FileSystemWatcher pulse);
    /// after each rebuild we re-apply the flag from this set. Cleared when the clipboard
    /// stops carrying our cut payload (WM_CLIPBOARDUPDATE detects the takeover).</summary>
    private readonly HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HWND of this window — cached at SourceInitialized so the Closed handler can
    /// unregister the clipboard listener without re-resolving the HwndSource.</summary>
    private IntPtr _clipboardOwnerHwnd;

    /// <summary>Live filter applied on top of <see cref="_items"/> via the default CollectionView.
    /// Empty / whitespace → no filter (every item passes). Refreshed on debounce-tick after the
    /// user finishes typing in <c>SearchBox</c>. Match is a case-insensitive substring against
    /// <see cref="WormholeItemViewModel.DisplayName"/>.</summary>
    private string _searchFilter = string.Empty;

    /// <summary>Debounce timer for the search box: a TextChanged tick resets this timer, and on
    /// fire (after Interval ms of quiet) we refresh the CollectionView. Keeps re-filter cost
    /// off the typing path so even a folder with thousands of items doesn't lag the box.</summary>
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>Drag-out gesture state. Captured on PreviewMouseLeftButtonDown over a tile,
    /// promoted to a real <c>DoDragDrop</c> on PreviewMouseMove once the OS drag threshold is
    /// exceeded. Without this the ListBox's Extended-selection rubber-band swallows the gesture
    /// and the user sees no file-cursor preview / no drop target acceptance outside the window.</summary>
    private Point? _itemDragStart;
    private WormholeItemViewModel? _itemDragSourceVm;

    public WormholeWindow(
        WormholeRecord record,
        Action onPersist,
        IconService icons,
        string wormholesRoot,
        Services.Wormholes.WormholeDefaultsService? defaults = null,
        Services.Wormholes.IWormholeWindowManager? manager = null)
    {
        _record = record;
        _onPersist = onPersist;
        _icons = icons;
        _wormholesRoot = wormholesRoot;
        _defaults = defaults;
        _manager = manager;
        InitializeComponent();
        DataContext = record;
        ItemsHost.ItemsSource = _items;

        // Wire the default CollectionView's predicate so SearchBox filters the visible tiles
        // without rebuilding the ObservableCollection. WPF's GetDefaultView returns the same
        // view ItemsHost is already iterating, so calling Refresh() after a filter change is
        // all that's needed for the WrapPanel to redraw.
        var view = CollectionViewSource.GetDefaultView(_items);
        if (view is not null) view.Filter = MatchesSearchFilter;

        // 250 ms is the sweet spot for incremental search: short enough that the result feels
        // live, long enough to skip the refilter on every keystroke for fast typists.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += OnSearchDebounceTick;

        Left = record.Geometry.X;
        Top = record.Geometry.Y;
        Width = record.Geometry.Width;
        Height = record.Geometry.Height;

        ApplyLockState();
        ApplyRollState();
        ApplyAppearance();

        RefreshPortalItems();
        EmptyStateHint.Text = "Folder is empty — drop files here to add them.";
        UpdateEmptyStateVisibility();

        // Wire the geometry-persist handlers on Loaded so WPF's initial reposition during the
        // very first Show() — which can clamp our requested Left/Top to the nearest visible
        // monitor's work area on PerMonitorV2 / multi-monitor setups — doesn't fire a persist
        // mid-boot. That synchronous SaveAsync from a startup-time LocationChanged caused
        // "Collection was modified" inside WormholeWindowManager.InitializeAsync's foreach
        // (the records list it was iterating was the same instance SaveAsync mutates).
        // After Loaded the position is settled, and any later LocationChanged is user-driven.
        Loaded += OnLoadedHookGeometryPersist;
        Closing += (_, _) =>
        {
            if (_isClosingFromManager) return;
            _onPersist();
        };
        // Ctrl+MouseWheel zooms tile size like Explorer's icon view. Tunneling handler on the
        // window so it fires regardless of whether the cursor is over the ListBox or the empty
        // state — and we can mark e.Handled = true before the ListBox sees it (which would
        // otherwise scroll the content while we're trying to zoom).
        PreviewMouseWheel += OnPreviewMouseWheelZoom;

        // Edge-resize via WM_NCHITTEST. WindowStyle=None + AllowsTransparency=True kills WPF's
        // native resize border; we synthesise hit-zones manually so the cursor switches to the
        // correct resize arrow within 8 px of any edge and Windows drives the actual resize.
        SourceInitialized += (_, _) =>
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var src = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            src?.AddHook(WindowProcResizeHook);
            // Register for clipboard change notifications so we can clear the "cut" tint on
            // selected items when the clipboard's content stops being ours (the user pressed
            // Ctrl+X then copied/cut something else, or some other process took over). The
            // hook stays installed for the window's lifetime; Closed unregisters.
            _clipboardOwnerHwnd = helper.Handle;
            _ = AddClipboardFormatListener(_clipboardOwnerHwnd);
            src?.AddHook(WindowProcClipboardHook);
        };
        Closed += (_, _) =>
        {
            if (_clipboardOwnerHwnd != IntPtr.Zero)
                _ = RemoveClipboardFormatListener(_clipboardOwnerHwnd);
        };
    }

    // WM_NCHITTEST handler. Constants from winuser.h: HTCLIENT=1, HTLEFT=10, HTRIGHT=11,
    // HTTOP=12, HTTOPLEFT=13, HTTOPRIGHT=14, HTBOTTOM=15, HTBOTTOMLEFT=16, HTBOTTOMRIGHT=17.
    // Returning any HT*EDGE/HT*CORNER tells DefWindowProc to begin a resize loop using the
    // appropriate cursor — no further code needed on our side. Skipped when the wormhole is
    // locked (must not resize) or rolled (only the header strip is visible; resize would
    // expose a torn layout).
    //
    // We also intercept WM_NCLBUTTONDBLCLK on those same edge codes: Windows' default is to
    // maximise vertically on HTTOP/HTBOTTOM dblclick (and horizontally on HTLEFT/HTRIGHT).
    // For a wormhole that's the wrong gesture — the user is usually trying to dblclick the
    // header to roll up but accidentally clipping the 8 px resize edge. Swallowing the
    // message keeps the vertical-maximise out of the way without disabling resize-drag.
    private IntPtr WindowProcResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCLBUTTONDBLCLK = 0x00A3;

        if (msg == WM_NCLBUTTONDBLCLK)
        {
            int ht = wParam.ToInt32();
            // Any edge or corner dblclick → swallow. HTCAPTION (=2) double-click is the
            // normal restore/maximise gesture and isn't returned by our hit test, so it
            // wouldn't land here anyway; this matches every HT code we DO return from below.
            if (ht is 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17)
            {
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        if (_record.IsLocked || _record.IsRolled) return IntPtr.Zero;

        // lParam packs screen X in the low word and screen Y in the high word. Sign-extension
        // matters on multi-monitor setups where the secondary monitor sits at negative coords.
        int xScreen = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int yScreen = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var pos = PointFromScreen(new Point(xScreen, yScreen));

        const double edge = 8;
        bool left   = pos.X >= 0 && pos.X < edge;
        bool right  = pos.X <= ActualWidth  && pos.X > ActualWidth  - edge;
        bool top    = pos.Y >= 0 && pos.Y < edge;
        bool bottom = pos.Y <= ActualHeight && pos.Y > ActualHeight - edge;

        int hit = (top, bottom, left, right) switch
        {
            (true,  _,    true,  _   ) => 13, // HTTOPLEFT
            (true,  _,    _,    true ) => 14, // HTTOPRIGHT
            (_,     true, true,  _   ) => 16, // HTBOTTOMLEFT
            (_,     true, _,    true ) => 17, // HTBOTTOMRIGHT
            (true,  _,    _,    _    ) => 12, // HTTOP
            (_,     true, _,    _    ) => 15, // HTBOTTOM
            (_,     _,    true, _    ) => 10, // HTLEFT
            (_,     _,    _,    true ) => 11, // HTRIGHT
            _ => 0,
        };
        if (hit == 0) return IntPtr.Zero;   // not on an edge — let WPF handle normally
        handled = true;
        return new IntPtr(hit);
    }

    /// <summary>Effective tile icon size for this wormhole. Fallback chain:
    /// per-wormhole override (<see cref="WormholeRecord.IconSizePx"/> &gt; 0) →
    /// app-wide default (<see cref="Services.Wormholes.WormholeDefaultsService.DefaultIconSizePx"/> &gt; 0) →
    /// user's Windows desktop icon size (<see cref="DesktopIconSize"/>). The middle layer is
    /// what the Settings → Wormholes "Default icon size" slider drives.</summary>
    private int EffectiveIconSize()
    {
        if (_record.IconSizePx > 0) return _record.IconSizePx;
        if (_defaults is { DefaultIconSizePx: > 0 } d) return d.DefaultIconSizePx;
        return DesktopIconSize.Get();
    }

    /// <summary>Tile padding read from the app-wide default. No per-wormhole override slot yet
    /// (one knob is plenty until proven otherwise) — every wormhole uses the same density set
    /// in Settings → Wormholes.</summary>
    private int EffectiveTilePadding() => _defaults?.DefaultTilePaddingPx ?? 4;

    /// <summary>App-wide line spacing (extra px between consecutive tile rows). No per-wormhole
    /// override — same rationale as <see cref="EffectiveTilePadding"/>.</summary>
    private int EffectiveLineSpacing() => _defaults?.DefaultLineSpacingPx ?? -4;

    /// <summary>App-wide label font size for the text under each icon. Falls back to 11 px
    /// (matches the previous hard-coded XAML default).</summary>
    private int EffectiveLabelFontSize() => _defaults?.DefaultLabelFontSizePx ?? 12;

    /// <summary>App-wide max label lines. Falls back to 2 (matches the previous wrap behaviour).</summary>
    private int EffectiveLabelMaxLines() => _defaults?.DefaultLabelMaxLines ?? 2;

    /// <summary>Effective opacity: per-wormhole override wins, else the app-wide default
    /// (<see cref="Services.Wormholes.WormholeDefaultsService.DefaultOpacity"/>). Final fallback
    /// is the legacy 0.95 that the XAML used to hardcode — preserves visuals if the manager
    /// somehow didn't wire a defaults service.</summary>
    private double EffectiveOpacity()
    {
        if (_record.Appearance.OpacityOverride is { } v) return v;
        return _defaults?.DefaultOpacity ?? 0.70;
    }

    /// <summary>Effective border opacity: app-wide default only for now (no per-wormhole
    /// override slot — kept consistent with the existing knob layout). Falls back to 1.0
    /// (legacy: fully opaque ring + shadow).</summary>
    private double EffectiveBorderOpacity() => _defaults?.DefaultBorderOpacity ?? 1.0;

    /// <summary>Apply opacity to the two backdrop layers — body + header — leaving the
    /// content overlay (icon tiles, labels, chrome buttons) at full opacity. Setting Opacity
    /// on the root <c>OuterFrame</c> would cascade to every descendant including the icons,
    /// which the user explicitly didn't want ("opacity solo allo sfondo e all'header").
    ///
    /// Border opacity is applied separately: we clone the theme's <c>OuterBorderBrush</c> into
    /// a per-window <c>SolidColorBrush</c> with the user's chosen alpha so the ring fades
    /// independently from the body. The drop shadow's effect opacity is multiplied by the same
    /// factor (baseline 0.45 × border) so the shadow disappears together with the ring rather
    /// than telegraphing the window's outline when the ring is invisible.</summary>
    private void ApplyAppearance()
    {
        var op = EffectiveOpacity();
        BodyBackdrop.Opacity = op;
        HeaderBackdrop.Opacity = op;

        var borderOp = EffectiveBorderOpacity();
        if (Application.Current?.Resources["OuterBorderBrush"] is System.Windows.Media.SolidColorBrush themed)
        {
            // Each wormhole gets its own brush — modifying the shared theme brush would fade
            // every wormhole's ring at once and also affect any other UI that consumes the
            // resource.
            var local = new System.Windows.Media.SolidColorBrush(themed.Color) { Opacity = borderOp };
            OuterFrame.BorderBrush = local;
        }
        if (OuterShadow is not null) OuterShadow.Opacity = 0.45 * borderOp;
    }

    /// <summary>Cheap path: only the opacity changed (Settings slider drag). Skips
    /// RebuildItems so the slider stays fluid even with many wormholes open.</summary>
    internal void RefreshOpacity() => ApplyAppearance();

    /// <summary>Expensive path: the icon size changed → every item VM has to be
    /// re-constructed so it asks <see cref="IconService.GetIconAtSize"/> for the new
    /// resolution. Use sparingly (typically once per NumberBox commit, not on every drag
    /// tick).</summary>
    internal void RefreshIconSize() => RebuildItems();

    private void OnPreviewMouseWheelZoom(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        // Step: 8 px per wheel tick. Enough granularity to land on Windows' standard preset
        // sizes (32 / 48 / 64 / 96) within 1–4 ticks while still feeling smooth.
        const int step = 8;
        const int min = 24;
        const int max = 256;
        var current = EffectiveIconSize();
        var next = e.Delta > 0 ? current + step : current - step;
        next = Math.Clamp(next, min, max);
        if (next == current) { e.Handled = true; return; }
        _record.IconSizePx = next;
        _onPersist();
        RebuildItems();
        e.Handled = true; // prevent ListBox from scrolling while the user is zooming
    }

    internal void CloseFromManager()
    {
        _isClosingFromManager = true;
        Close();
    }

    /// <summary>Pick a sensible owner for MessageBox / sub-dialogs spawned from the wormhole.
    /// We never use <c>this</c> because the wormhole is parented to <c>WorkerW</c> (desktop
    /// layer) — a dialog inheriting that parenting falls behind every other app the moment it
    /// shows. <c>Application.MainWindow</c> (the AresToys settings window) is normally hidden
    /// but its handle is created, which is enough to give MessageBox a real top-level owner;
    /// when it's null we let MessageBox position itself at screen centre, no owner.</summary>
    private static Window? OwnerForDialogs()
    {
        var main = Application.Current?.MainWindow;
        if (main is null) return null;
        var helper = new System.Windows.Interop.WindowInteropHelper(main);
        return helper.Handle != IntPtr.Zero ? main : null;
    }

    /// <summary>Re-apply title / lock / roll state from the underlying record. Called by
    /// <see cref="AresToys.App.Services.Wormholes.WormholeWindowManager.ReconcileAsync"/> when the
    /// record was mutated from outside the chrome (typically the Wormholes Settings tab toggling
    /// Lock or renaming). <c>WormholeRecord</c> isn't INPC, so we force the DataContext re-bind
    /// in the same way <see cref="CommitInlineRename"/> does.</summary>
    public void RefreshFromRecord()
    {
        DataContext = null;
        DataContext = _record;
        ApplyLockState();
        ApplyRollState();
    }

    /// <summary>Re-enumerate the watched folder and rebuild the item tiles. Called by the
    /// manager after the global icon-size / tile-padding defaults change so the live window
    /// adopts the new dimensions without restart.</summary>
    internal void RebuildItems()
    {
        RefreshPortalItems();
        UpdateEmptyStateVisibility();
    }

    /// <summary>One-shot Loaded handler that subscribes the geometry-persist hooks AFTER the
    /// initial Show + reposition has settled. Self-unsubscribes so a re-Loaded event (rare for
    /// a top-level window but possible) doesn't double-subscribe and double-persist every drag.</summary>
    private void OnLoadedHookGeometryPersist(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedHookGeometryPersist;
        // Re-apply opacity AFTER the window has been added to the visual tree and the first
        // render pass has settled. AllowsTransparency=True + the DropShadowEffect on OuterFrame
        // can produce a "low-opacity-looking" initial paint until the first activation/composition
        // tick — observed as: wormhole opens visually more transparent than the configured 95 %,
        // becomes properly opaque the moment the user clicks anywhere on it. Doing this on
        // ContentRendered (after the first frame is shown) forces WPF to repaint the backdrops
        // with the right alpha BEFORE the user has to click to wake it up.
        Dispatcher.BeginInvoke(new Action(ApplyAppearance), System.Windows.Threading.DispatcherPriority.Render);
        LocationChanged += (_, _) =>
        {
            if (_isClosingFromManager) return;
            _record.Geometry.X = Left;
            _record.Geometry.Y = Top;
            _onPersist();
        };
        SizeChanged += (_, args) =>
        {
            if (_isClosingFromManager) return;
            if (!args.HeightChanged && !args.WidthChanged) return;
            _record.Geometry.Width = Width;
            if (!_record.IsRolled)
            {
                _record.Geometry.Height = Height;
                _record.Geometry.UnrolledHeight = Height;
            }
            _onPersist();
        };
    }

    public event EventHandler<Guid>? DeleteRequested;

    /// <summary>Re-enumerate the source folder and rebuild <see cref="_items"/>. Called from
    /// the manager's <c>FolderWatcher.Changed</c> handler (debounced 300 ms) and from the
    /// hamburger "Refresh" entry.</summary>
    public void RefreshPortalItems()
    {
        var portal = _record.Portal;
        if (portal is null) return;

        _items.Clear();
        _portalItemCapReached = false;
        try
        {
            if (!Directory.Exists(portal.SourcePath))
            {
                // Source went away (drive ejected, folder deleted, manual rename): swap to
                // the dedicated error panel which shows the missing path + a "Pick a new
                // folder…" button so the user can repoint without going through the chrome
                // hamburger. EmptyStateHint stays hidden — keeping both visible would
                // crowd the small wormhole real estate.
                ShowSourceMissingState(portal.SourcePath);
                return;
            }
            // Path resolves again — back to the normal empty / populated states.
            SourceMissingPanel.Visibility = Visibility.Collapsed;
            EmptyStateHint.Text = "Folder is empty — drop files here to add them.";

            // Source enumeration: folders + files (or just files if the user disabled subdir
            // listing). The sort mode (Name / Modified / Type) chosen via the hamburger menu
            // is applied in SortPortalEntries; folders are always grouped first regardless.
            IEnumerable<string> entries = portal.IncludeSubdirectoriesAsItems
                ? Directory.EnumerateDirectories(portal.SourcePath).Concat(Directory.EnumerateFiles(portal.SourcePath))
                : Directory.EnumerateFiles(portal.SourcePath);

            var ordered = SortPortalEntries(entries, portal.SortMode).ToList();
            foreach (var path in ordered)
            {
                if (_items.Count >= PortalItemCap)
                {
                    _portalItemCapReached = true;
                    break;
                }
                _items.Add(new WormholeItemViewModel(
                    path, _icons,
                    EffectiveIconSize(), EffectiveTilePadding(), EffectiveLineSpacing(),
                    EffectiveLabelFontSize(), EffectiveLabelMaxLines()));
            }
            // Re-apply the "cut" tint on the freshly-built VMs — the previous instances were
            // dropped along with their IsCutMarked flag, but the path set is still live as
            // long as the clipboard carries it.
            ReapplyCutMarks();
        }
        catch (UnauthorizedAccessException)
        {
            EmptyStateHint.Text = "Access denied to source folder.";
        }
        catch (Exception)
        {
            // Defensive: rare IO failures shouldn't crash the wormhole. The next debounce tick
            // will retry.
            EmptyStateHint.Text = "Couldn't read source folder.";
        }

        UpdateEmptyStateVisibility();
        if (_portalItemCapReached)
        {
            // The cap is rare enough that a MessageBox would feel heavy on every refresh; we
            // surface it via the empty-state slot so it stays visible but unobtrusive even
            // while items render around it.
            EmptyStateHint.Text = $"Folder has more than {PortalItemCap} entries — only the first {PortalItemCap} are shown.";
            EmptyStateHint.Visibility = Visibility.Visible;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Header gestures
    // -----------------------------------------------------------------------------------------

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _manager?.NotifyWormholeFocused(this);
        // Middle-click anywhere on the header → open the source folder in Explorer. Available
        // even when the wormhole is locked (read-only gesture, doesn't move/resize the window)
        // and ignores click count so a quick Browser-style middle-click works on the first try.
        if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
        {
            OpenSourceFolder();
            e.Handled = true;
            return;
        }
        if (_record.IsLocked) return;
        if (e.ClickCount == 2) { ToggleRoll(); return; }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Header search box
    // -----------------------------------------------------------------------------------------

    /// <summary>Click on the magnifying-glass icon → expand the inline search field over the
    /// same header slot. Width of the search column is bumped from Auto (28 px button) to a
    /// fixed pixel so the title column still gets the remaining stretch space and doesn't
    /// crunch the wormhole title.</summary>
    private void OnSearchIconClicked(object sender, RoutedEventArgs e)
    {
        OpenSearchBox(takeFocus: true);
    }

    private void OpenSearchBox(bool takeFocus)
    {
        SearchIconButton.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        // Pin the column to a comfortable width so the textbox actually has somewhere to go;
        // Auto would collapse to the textbox's MinWidth (~24 px) and feel cramped.
        SearchSlotColumn.Width = new GridLength(160);
        if (takeFocus)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    /// <summary>Collapse the search field back into the icon button. Clears the filter as well
    /// so the wormhole returns to showing every item; if the user wanted to keep the filter
    /// active they would leave the textbox visible (LostFocus with non-empty text doesn't
    /// trigger this path).</summary>
    private void CloseSearchBox()
    {
        SearchBox.Text = string.Empty;
        _searchFilter = string.Empty;
        _searchDebounce.Stop();
        ApplySearchFilter();
        SearchBox.Visibility = Visibility.Collapsed;
        SearchIconButton.Visibility = Visibility.Visible;
        SearchSlotColumn.Width = GridLength.Auto;
    }

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Restart the debounce timer on every keystroke — only when the user stops typing for
        // the full interval do we actually re-filter. Stop+Start is the canonical "reset"
        // pattern for DispatcherTimer.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _searchFilter = SearchBox.Text?.Trim() ?? string.Empty;
        ApplySearchFilter();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearchBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // Commit the current text immediately (skip the debounce) and drop keyboard focus
            // so a subsequent Esc / click anywhere goes to the canvas / header. Don't collapse
            // the textbox — the user may want to refine the filter further.
            _searchDebounce.Stop();
            _searchFilter = SearchBox.Text?.Trim() ?? string.Empty;
            ApplySearchFilter();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        // Lost focus + empty text → snap back to icon mode. Non-empty text leaves the textbox
        // visible so the user can see (and edit / clear) the active filter without re-opening
        // the search box.
        if (string.IsNullOrEmpty(SearchBox.Text)) CloseSearchBox();
    }

    private void ApplySearchFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_items);
        view?.Refresh();
    }

    /// <summary>Predicate plugged into the default CollectionView. Empty filter passes
    /// everything (the typical case). Otherwise: case-insensitive substring match against
    /// DisplayName so a user typing "rep" sees "Report.docx", "ReplaceMe.txt", "rep-notes",
    /// etc.</summary>
    private bool MatchesSearchFilter(object item)
    {
        if (string.IsNullOrEmpty(_searchFilter)) return true;
        return item is WormholeItemViewModel vm
            && vm.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Launch Explorer on the wormhole's source folder. Mirrors the Wormhole row's
    /// OpenFolder command in the settings tab — duplicated rather than reaching across to the
    /// VM because this window has its own _record snapshot and there's no row VM bound to it.
    /// Silent no-op when the path is empty (broken/unlinked wormhole) or Explorer launch fails;
    /// the user already sees the folder-missing chrome in that case.</summary>
    private void OpenSourceFolder()
    {
        var folder = _record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Explorer failed (folder missing / drive offline / ACL); the visible error chrome
            // on the wormhole already advertises the broken state, no need to nag with a dialog.
        }
    }

    private void OnContentAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        _manager?.NotifyWormholeFocused(this);
        if (_record.IsLocked) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void OnChevronClicked(object sender, RoutedEventArgs e) => ToggleRoll();

    private void OnLockClicked(object sender, RoutedEventArgs e)
    {
        _record.IsLocked = !_record.IsLocked;
        ApplyLockState();
        _onPersist();
    }

    // -----------------------------------------------------------------------------------------
    // Hamburger menu
    // -----------------------------------------------------------------------------------------

    private void OnHamburgerClicked(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openFolder = new System.Windows.Controls.MenuItem { Header = "Open folder" };
        openFolder.Click += (_, _) => OpenAssociatedFolder();
        menu.Items.Add(openFolder);

        var refresh = new System.Windows.Controls.MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => RefreshPortalItems();
        menu.Items.Add(refresh);

        var changeFolder = new System.Windows.Controls.MenuItem { Header = "Change folder…" };
        changeFolder.Click += (_, _) => OnChangeFolder();
        menu.Items.Add(changeFolder);

        // Sort by submenu. The chosen mode persists on the per-record Portal config and the
        // items list rebuilds immediately.
        var sortBy = new System.Windows.Controls.MenuItem { Header = "Sort by" };
        var sortModes = new[] { "Name", "Modified", "Type" };
        var currentSort = GetCurrentSortMode();
        foreach (var mode in sortModes)
        {
            var captured = mode;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = mode,
                IsCheckable = true,
                IsChecked = string.Equals(currentSort, captured, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = false,
            };
            item.Click += (_, _) => SetSortMode(captured);
            sortBy.Items.Add(item);
        }
        menu.Items.Add(sortBy);

        // Reset zoom: visible only when this wormhole carries a per-record override
        // (Ctrl+Wheel was used at some point). Clearing it falls back through the chain
        // to the app-wide default + DesktopIconSize, which the user can read off the
        // Settings → Wormholes panel.
        if (_record.IconSizePx > 0)
        {
            var resetZoom = new System.Windows.Controls.MenuItem { Header = "Reset zoom to default" };
            resetZoom.Click += (_, _) =>
            {
                _record.IconSizePx = 0;
                _onPersist();
                RebuildItems();
            };
            menu.Items.Add(resetZoom);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename" };
        rename.Click += (_, _) => BeginInlineRename();
        menu.Items.Add(rename);

        var hide = new System.Windows.Controls.MenuItem { Header = "Hide this wormhole" };
        hide.Click += (_, _) =>
        {
            _record.IsHidden = true;
            _onPersist();
            CloseFromManager();
        };
        menu.Items.Add(hide);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete wormhole…" };
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(OwnerForDialogs(),
                $"Delete wormhole \"{_record.Title}\"?\n\nThe source folder on disk is NOT touched.",
                "AresToys",
                MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;

            // Second prompt: if the source folder is on disk AND empty, offer to remove it.
            // Never auto-delete a folder with content — that's the user's data and the first
            // dialog promised we wouldn't touch it. Empty folders are a common leftover after a
            // "create wormhole, move stuff elsewhere, delete wormhole" workflow.
            if (_record.Portal is { SourcePath: { Length: > 0 } src }
                && Directory.Exists(src) && IsEmptyDirectory(src))
            {
                var alsoDelete = MessageBox.Show(OwnerForDialogs(),
                    $"The source folder is empty:\n\n  {src}\n\nDelete it from disk as well?",
                    "AresToys", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (alsoDelete == MessageBoxResult.Yes)
                {
                    try { Directory.Delete(src); }
                    catch (Exception ex)
                    {
                        // Best-effort: a permission denied / locked-by-AV failure here shouldn't
                        // block the wormhole record deletion below. Inform the user so they can
                        // clean up by hand if they care.
                        MessageBox.Show(OwnerForDialogs(),
                            "The wormhole was deleted, but the empty folder couldn't be removed:\n" + ex.Message,
                            "AresToys", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            DeleteRequested?.Invoke(this, _record.Id);
        };
        menu.Items.Add(delete);

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
    }

    private void OpenAssociatedFolder()
    {
        var folder = _record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(OwnerForDialogs(),"No folder is associated with this wormhole.", "AresToys",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerForDialogs(),"Couldn't open the folder:\n" + ex.Message,
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Toggle the wormhole into the "source folder unavailable" state — hides the
    /// items + empty-state hint, surfaces the dedicated panel with the missing path and a
    /// re-link button. Called from <see cref="RefreshPortalItems"/> when Directory.Exists
    /// returns false. The watcher keeps the old path; if the folder reappears (USB plugged
    /// back in, network share resumes) the next refresh tick clears this state automatically.</summary>
    private void ShowSourceMissingState(string missingPath)
    {
        SourceMissingPath.Text = string.IsNullOrEmpty(missingPath) ? "(no path set)" : missingPath;
        SourceMissingPanel.Visibility = Visibility.Visible;
        EmptyStateHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Re-link button on the source-missing error panel. Reuses the same folder-pick
    /// flow as the hamburger "Change folder…" entry — keeps a single place where the
    /// SourcePath mutation + persist + refresh live.</summary>
    private void OnRelinkSourceClicked(object sender, RoutedEventArgs e) => OnChangeFolder();

    private void OnChangeFolder()
    {
        if (_record.Portal is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the new source folder for this wormhole",
            InitialDirectory = Directory.Exists(_record.Portal.SourcePath)
                ? _record.Portal.SourcePath
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != true) return;
        _record.Portal.SourcePath = dlg.FolderName;
        _onPersist();
        // The manager listens for an explicit event to re-Start its watcher on the new path.
        // For the MVP we just refresh once and let the user toggle Refresh manually; the
        // watcher will reattach correctly on the next app launch. (Live watcher swap = polish.)
        RefreshPortalItems();
    }

    // -----------------------------------------------------------------------------------------
    // Inline rename
    // -----------------------------------------------------------------------------------------

    private void BeginInlineRename()
    {
        TitleEditor.Text = _record.Title;
        TitleEditor.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void CommitInlineRename()
    {
        var next = (TitleEditor.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(next) && next != _record.Title)
        {
            _record.Title = next;
            DataContext = null;
            DataContext = _record;
            _onPersist();
        }
        EndInlineRename();
    }

    private void EndInlineRename()
    {
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void OnTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitInlineRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndInlineRename(); e.Handled = true; }
    }

    private void OnTitleEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (TitleEditor.Visibility == Visibility.Visible) CommitInlineRename();
    }

    // -----------------------------------------------------------------------------------------
    // Drop handling — branches on Data vs Portal kind
    // -----------------------------------------------------------------------------------------

    /// <summary>Latched flag tracking whether the in-progress drag is using the right mouse
    /// button. We sample it on every DragOver tick because at OnDrop time the button is already
    /// being released and the flag in the final <see cref="DragEventArgs.KeyStates"/> may have
    /// cleared. Used to switch the drop path from "default move/copy" to the Explorer-style
    /// "Copy here / Move here / Create shortcut here / Cancel" context menu.</summary>
    private bool _lastDragWasRightButton;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _lastDragWasRightButton = (e.KeyStates & DragDropKeyStates.RightMouseButton) != 0;
        e.Effects = ResolveDropEffect(e);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _lastDragWasRightButton = (e.KeyStates & DragDropKeyStates.RightMouseButton) != 0;
        e.Effects = ResolveDropEffect(e);
        e.Handled = true;
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e)
    {
        if (_record.IsLocked) return DragDropEffects.None;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        // Data wormhole always reads as Copy — the original source file is never touched, only
        // a .lnk is materialised. Cursor reflects this so the user can't confuse it with a
        // real file move.
        // Explorer's standard drag semantics:
        //   - Ctrl  → force Copy
        //   - Shift → force Move
        //   - no modifier → Move if source and dest are on the same volume, Copy otherwise
        // The cursor previews the action of the first dragged path; per-file decisions in
        // DropOntoPortal apply the same rule per item, so a mixed-volume batch behaves
        // correctly even if the cursor only shows one of the two effects.
        var ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey;
        if (ctrl)  return DragDropEffects.Copy;
        if (shift) return DragDropEffects.Move;

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return DragDropEffects.Copy;
        return IsSameVolume(paths[0], _record.Portal?.SourcePath)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    /// <summary>True if <paramref name="source"/> and <paramref name="destFolder"/> share the
    /// same path root — drive letter for local paths, UNC server+share for network paths. Used
    /// to decide the default Move-vs-Copy behaviour on a Portal drop (Explorer semantics —
    /// same-volume drag defaults to Move, cross-volume defaults to Copy).</summary>
    private static bool IsSameVolume(string source, string? destFolder)
    {
        if (string.IsNullOrWhiteSpace(destFolder)) return false;
        try
        {
            var srcRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(source));
            var dstRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destFolder));
            return !string.IsNullOrEmpty(srcRoot)
                && string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Defensive — GetFullPath throws on bad paths (illegal chars, too-long). Fall back
            // to Copy on uncertainty so we never silently move a file we couldn't reason about.
            return false;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Per-item drop routing — when the user drops something on a SPECIFIC tile (not the empty
    // wormhole area), behaviour switches based on the tile's target:
    //   - Folder (or .lnk → folder): file goes INSIDE the folder, not into the wormhole's
    //     source folder. Same gesture Explorer / Portals / Stardock Fences use.
    //   - Executable / script (or .lnk → executable): file is passed as an argument to that
    //     program, so dragging an image onto a Photoshop shortcut opens the image in
    //     Photoshop, dragging a path onto a .bat passes the path as %1, etc.
    //   - Anything else (text file, doc, image with no associated handler we recognise):
    //     fall back to the wormhole-level drop (copy into source folder) so the user isn't
    //     left without a sensible default.
    // e.Handled = true on the item path stops the event bubbling up to the container's
    // OnDrop, so the wormhole doesn't double-process the same drop.
    // -----------------------------------------------------------------------------------------

    private void OnItemDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = ResolveItemDropEffect(sender, e);
        e.Handled = true;
    }

    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveItemDropEffect(sender, e);
        e.Handled = true;
    }

    /// <summary>Pick the drop effect cursor when hovering a specific tile. Folder targets show
    /// Move/Copy (same heuristic as the wormhole-level drop); executable targets show Link
    /// (the closest cursor for "drop = open with this program"). Locked wormholes still allow
    /// drops on individual items — dropping a file onto Photoshop.lnk inside a locked
    /// wormhole has no side effect on the wormhole itself, so the lock state is irrelevant.</summary>
    private DragDropEffects ResolveItemDropEffect(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        if (sender is not FrameworkElement fe || fe.DataContext is not WormholeItemViewModel vm)
            return DragDropEffects.None;

        var resolved = ResolveShellTarget(vm.AbsolutePath);
        if (Directory.Exists(resolved))
        {
            // Folder drop — same Move/Copy heuristic as the wormhole-level drop
            var ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            var shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey;
            if (ctrl) return DragDropEffects.Copy;
            if (shift) return DragDropEffects.Move;
            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths is null || paths.Length == 0) return DragDropEffects.Copy;
            return IsSameVolume(paths[0], resolved) ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if (IsExecutableTarget(resolved))
        {
            // "Open with this program" — Link cursor is what Explorer shows when you drag a
            // file onto an executable. Drop fires the exe with the dropped file as argument.
            return DragDropEffects.Link;
        }
        // Unknown target type — let the wormhole-level handler take over by signalling None
        // here. The event still bubbles because we don't set Handled in the None branch.
        return DragDropEffects.None;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not WormholeItemViewModel vm) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return;

        var resolved = ResolveShellTarget(vm.AbsolutePath);

        // Folder target → drop the files inside it. SHFileOperation handles the shell prompts
        // (rename on conflict, progress for big batches) automatically.
        if (Directory.Exists(resolved))
        {
            var ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            var shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey;
            // Per-file Move-vs-Copy mirrors the wormhole-level rule.
            var move = !ctrl && (shift || IsSameVolume(paths[0], resolved));
            ShellCopyOrMove(paths, resolved, move);
            e.Handled = true;
            return;
        }

        // Executable / script target → launch it with the dropped file as argument(s).
        // Multiple files become a space-separated list, each quoted so paths-with-spaces stay
        // intact. UseShellExecute = true so .lnk resolution / Verb=runas still apply if the
        // user pinned that on the shortcut itself.
        if (IsExecutableTarget(resolved))
        {
            try
            {
                var args = string.Join(' ', paths.Select(p => $"\"{p}\""));
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vm.AbsolutePath, // launch via the cell's actual path so .lnk
                                                 // working dir + args + verb get applied;
                                                 // resolving to the target would strip those.
                    Arguments = args,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(resolved) ?? string.Empty,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerForDialogs(),
                    $"Couldn't open with {Path.GetFileName(resolved)}:\n{ex.Message}",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            e.Handled = true;
            return;
        }

        // Unknown target — let the container-level drop take over (copy into the source
        // folder). NOT marking e.Handled = true lets the event bubble.
    }

    /// <summary>Recognised executable / script extensions that accept a file path as their
    /// first argument. Drop-target detection uses this; anything outside the set falls back
    /// to the wormhole-level drop. Lowercase + leading dot for direct comparison against
    /// Path.GetExtension output.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".wsf", ".com",
    };

    private static bool IsExecutableTarget(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (Directory.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ExecutableExtensions.Contains(ext);
    }

    /// <summary>Walk through a .lnk to get the underlying target path. Returns the input path
    /// untouched for non-.lnk inputs. .url files (web shortcuts) are NOT resolved — Process.
    /// Start handles them by launching the browser, no resolution needed. Failures during
    /// COM resolution fall back to the raw path, which is correct enough for the executable-
    /// vs-folder branching downstream (a broken .lnk falls through to the "unknown" branch
    /// and goes via container drop).</summary>
    private static string ResolveShellTarget(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return path;
        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(260 * 2); // MAX_PATH wide
                try
                {
                    link.GetPath(buffer, 260, IntPtr.Zero, 0);
                    var resolved = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
                    return string.IsNullOrEmpty(resolved) ? path : resolved;
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(link); }
        }
        catch
        {
            // Broken .lnk / COM unavailable → return the raw path. Lets the caller decide
            // the fallback (unknown target type → bubble to container drop).
            return path;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_record.IsLocked) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return;
        e.Handled = true;

        // Right-button drag → show the Explorer-style choice menu instead of executing the
        // default move/copy heuristic. Latched in DragOver above because the right button has
        // already been released by the time OnDrop fires, so reading e.KeyStates here would
        // give a false negative.
        if (_lastDragWasRightButton)
        {
            ShowRightDragDropMenu(paths);
            _lastDragWasRightButton = false;
            return;
        }
        _lastDragWasRightButton = false;
        DropOntoPortal(paths, RightDragChoice.None,
            ctrl:  (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey,
            shift: (e.KeyStates & DragDropKeyStates.ShiftKey)   == DragDropKeyStates.ShiftKey);
    }

    /// <summary>Forced choice for a right-button drag-and-drop. None = use the legacy
    /// Move/Copy heuristic (Ctrl/Shift modifiers + same-volume rule); the other values bypass
    /// the heuristic and run the requested operation on every source path.</summary>
    private enum RightDragChoice { None, Copy, Move, Shortcut }

    /// <summary>Build + show the Explorer-style "Copy here / Move here / Create shortcut here /
    /// Cancel" menu at the current mouse position. Uses a plain WPF ContextMenu — wpf-ui's
    /// dark theme picks it up automatically, so the chrome matches the rest of AresToys
    /// without a custom template. Each entry routes back through <see cref="DropOntoPortal"/>
    /// with the forced choice set.</summary>
    private void ShowRightDragDropMenu(string[] paths)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        var copy = new System.Windows.Controls.MenuItem { Header = "Copy here" };
        copy.Click += (_, _) => DropOntoPortal(paths, RightDragChoice.Copy, ctrl: false, shift: false);
        menu.Items.Add(copy);

        var move = new System.Windows.Controls.MenuItem { Header = "Move here" };
        move.Click += (_, _) => DropOntoPortal(paths, RightDragChoice.Move, ctrl: false, shift: false);
        menu.Items.Add(move);

        var shortcut = new System.Windows.Controls.MenuItem { Header = "Create shortcut here" };
        shortcut.Click += (_, _) => DropOntoPortal(paths, RightDragChoice.Shortcut, ctrl: false, shift: false);
        menu.Items.Add(shortcut);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Cancel = dismiss the menu, do nothing. The menu auto-closes on click anywhere else
        // (Esc / click-outside), Cancel just gives the user an explicit out to match Explorer.
        var cancel = new System.Windows.Controls.MenuItem { Header = "Cancel" };
        menu.Items.Add(cancel);

        menu.IsOpen = true;
    }

    private void DropOntoPortal(string[] paths, RightDragChoice choice, bool ctrl, bool shift)
    {
        if (_record.Portal is null || string.IsNullOrWhiteSpace(_record.Portal.SourcePath)) return;
        var dest = _record.Portal.SourcePath;
        if (!Directory.Exists(dest))
        {
            MessageBox.Show(OwnerForDialogs(),"The Portal source folder isn't currently available.",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Per-file Move-vs-Copy decision matches the cursor preview rule in ResolveDropEffect:
        // Ctrl forces Copy, Shift forces Move, otherwise same-volume drags Move and cross-volume
        // drags Copy. Evaluated per item so a mixed-volume batch (e.g. user drags 2 files from
        // C:\ and 1 from D:\ at the same time) gets the right behaviour for each.
        //
        // RightDragChoice (when the menu picked an explicit verb) overrides everything: the
        // user told us exactly what to do, no heuristics.
        bool ShouldMove(string source) => choice switch
        {
            RightDragChoice.Move     => true,
            RightDragChoice.Copy     => false,
            RightDragChoice.Shortcut => false, // shortcut path handled separately below
            _                        => !ctrl && (shift || IsSameVolume(source, dest)),
        };

        var errors = new List<string>();
        foreach (var source in paths)
        {
            try
            {
                // Skip a self-drop (dragging an item from the same source folder back in) —
                // the operation would either be a same-path Move (NOP) or a Copy that would
                // create "name (2)" pointlessly. Exception: Shortcut creation on a self-drop
                // is still valid (creates a .lnk next to the original — same as Explorer).
                var srcDir = Directory.Exists(source)
                    ? Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar))
                    : Path.GetDirectoryName(source);
                if (string.Equals(srcDir, dest, StringComparison.OrdinalIgnoreCase)
                    && choice != RightDragChoice.Shortcut) continue;

                // Block any drop that would put a folder inside itself or one of its descendants
                // (recursion-into-self). Catches: dropping the wormhole's own source folder onto
                // the wormhole (would create C:\Foo\Foo), or dropping any parent folder onto a
                // Portal whose source is a descendant (would infinite-loop Directory.Move /
                // CopyDirectoryRecursive). Only relevant when source is a directory — file drops
                // can't recurse.
                if (IsDestinationInsideSource(source, dest))
                {
                    errors.Add($"{Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))}: can't drop a folder into itself or one of its subfolders.");
                    continue;
                }

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                // Shortcut path: write a .lnk next to the source path. Explorer uses
                // "<name> - Shortcut.lnk" as the default filename when creating shortcuts via
                // its right-click drag menu; we match that for consistency.
                if (choice == RightDragChoice.Shortcut)
                {
                    var shortcutName = $"{name} - Shortcut.lnk";
                    var shortcutPath = UniqueTargetPath(Path.Combine(dest, shortcutName));
                    CreateShellShortcut(source, shortcutPath);
                    continue;
                }

                var targetPath = Path.Combine(dest, name);
                targetPath = UniqueTargetPath(targetPath);

                var move = ShouldMove(source);
                if (Directory.Exists(source))
                {
                    if (move) Directory.Move(source, targetPath);
                    else CopyDirectoryRecursive(source, targetPath);
                }
                else
                {
                    if (move) File.Move(source, targetPath, overwrite: false);
                    else File.Copy(source, targetPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }
        // No manual refresh — the FolderWatcher will pick the changes up after its 300 ms tick
        // and the manager calls RefreshPortalItems(). Only show errors here.
        if (errors.Count > 0)
            MessageBox.Show(OwnerForDialogs(),"Some items couldn't be moved/copied:\n\n" + string.Join("\n", errors),
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>True if <paramref name="dest"/> is the same path as <paramref name="source"/>
    /// or a descendant directory of it. Used to block recursive moves / copies that would
    /// produce <c>C:\Foo\Foo</c> or trigger an infinite copy loop when the user drops a folder
    /// onto one of its own subtrees. Source-is-file → always false (files can't recurse).</summary>
    private static bool IsDestinationInsideSource(string source, string dest)
    {
        if (!Directory.Exists(source)) return false;
        try
        {
            var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destFull   = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(destFull, sourceFull, StringComparison.OrdinalIgnoreCase)
                || destFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Defensive: GetFullPath can throw on illegal characters / too-long paths. Treat as
            // "not same" (= allow the drop) — File.Move / Directory.Move will surface their own
            // exception to the per-item error list if it really is a bad path.
            return false;
        }
    }

    private static string UniqueTargetPath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var dir = Path.GetDirectoryName(candidate)!;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var n = 2; n < 1000; n++)
        {
            var next = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    // -----------------------------------------------------------------------------------------
    // Sort by — hamburger menu + persistence
    // -----------------------------------------------------------------------------------------

    private string GetCurrentSortMode() => _record.Portal?.SortMode ?? "Name";

    private void SetSortMode(string mode)
    {
        if (string.IsNullOrEmpty(mode) || _record.Portal is null) return;
        _record.Portal.SortMode = mode;
        _onPersist();
        RefreshPortalItems();
    }

    /// <summary>Order Portal entries: folders first (alphabetical), then files by chosen sort
    /// mode. Folders-first mirrors Stardock and Explorer conventions — the user expects to see
    /// directories grouped at the top regardless of the file-side sort.</summary>
    private static IEnumerable<string> SortPortalEntries(IEnumerable<string> entries, string sortMode)
    {
        var materialised = entries as IList<string> ?? entries.ToList();
        var folders = materialised.Where(Directory.Exists)
            .OrderBy(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                     StringComparer.CurrentCultureIgnoreCase);
        var files = materialised.Where(p => !Directory.Exists(p));
        files = sortMode switch
        {
            "Modified" => files.OrderByDescending(p => SafeLastWriteTime(p)),
            "Type"     => files.OrderBy(p => Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)
                                .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase),
            _          => files.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase),
        };
        return folders.Concat(files);
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>True if the directory exists and has zero entries (files OR subdirectories).
    /// EnumerateFileSystemEntries is cheaper than GetFiles+GetDirectories — it streams the first
    /// entry and we early-out. Any IO failure is treated as "not empty" so the delete-folder
    /// prompt errs on the side of preserving the folder.</summary>
    private static bool IsEmptyDirectory(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }

    private void UpdateEmptyStateVisibility()
    {
        EmptyStateHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WormholeItemViewModel vm) return;
        OpenItem(vm);
    }

    // -----------------------------------------------------------------------------------------
    // Drag-out gesture — drag a tile out of the wormhole into Explorer / desktop / another
    // wormhole. Without this handler the inner ListBox's Extended-selection rubber-band would
    // swallow the gesture: the user would see selection extend across other tiles instead of
    // the standard file-cursor preview, and dropping outside the window would land nowhere.
    //
    // Preview* variants so we run BEFORE the ListBoxItem's bubbling MouseLeftButtonDown handler
    // (which is what kicks off marquee selection). We only RECORD the start point on Down —
    // selection still updates normally because we don't set Handled. PreviewMouseMove promotes
    // to DoDragDrop once SystemParameters.MinimumHorizontal/VerticalDragDistance is exceeded,
    // so a quick click without movement still selects/double-clicks normally.
    //
    // Payload: DataFormats.FileDrop with absolute paths — this is what makes Windows show the
    // file-icon preview cursor and lets Explorer / desktop / other wormholes accept the drop.
    // Multi-select: if the armed item is part of the current selection we drag every selected
    // path (Explorer multi-drag); otherwise just the armed one. Locked wormholes restrict the
    // allowed effects to Copy|Link so the source folder can't be mutated by a Move drop.
    // -----------------------------------------------------------------------------------------

    private void OnItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) { _itemDragStart = null; _itemDragSourceVm = null; return; }
        if (fe.DataContext is not WormholeItemViewModel vm) { _itemDragStart = null; _itemDragSourceVm = null; return; }
        _itemDragStart = e.GetPosition(this);
        _itemDragSourceVm = vm;
    }

    private void OnItemPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _itemDragStart = null;
        _itemDragSourceVm = null;
    }

    private void OnItemPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_itemDragStart is null || _itemDragSourceVm is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _itemDragStart = null; _itemDragSourceVm = null; return; }
        var pos = e.GetPosition(this);
        var dx = Math.Abs(pos.X - _itemDragStart.Value.X);
        var dy = Math.Abs(pos.Y - _itemDragStart.Value.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        var armedVm = _itemDragSourceVm;
        _itemDragStart = null;
        _itemDragSourceVm = null;
        if (sender is not FrameworkElement fe) return;

        // Multi-drag if armed item is part of the live selection; otherwise single-item drag.
        // Mirrors Explorer: clicking an unselected item then dragging drags only that item.
        string[] paths;
        if (ItemsHost.SelectedItems.Contains(armedVm))
        {
            paths = ItemsHost.SelectedItems
                .OfType<WormholeItemViewModel>()
                .Select(v => v.AbsolutePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
        }
        else
        {
            paths = string.IsNullOrEmpty(armedVm.AbsolutePath)
                ? Array.Empty<string>()
                : new[] { armedVm.AbsolutePath };
        }
        if (paths.Length == 0) return;

        var data = new DataObject();
        var sc = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths) sc.Add(p);
        data.SetFileDropList(sc);

        // Locked wormhole: forbid Move so the source folder stays intact. Copy / shortcut still
        // allowed so the user can pull a reference out without mutating the locked contents.
        var allowed = _record.IsLocked
            ? DragDropEffects.Copy | DragDropEffects.Link
            : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

        try
        {
            DragDrop.DoDragDrop(fe, data, allowed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WormholeWindow: drag-out failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Per-item context menu — always the Windows shell native menu (same one Explorer shows).
    // The custom AresToys curated menu (Open / Open file location / Copy path / Move to /
    // Rename / Delete) was removed in favour of the native one: Explorer's menu already covers
    // every entry we had AND every third-party verb the user has installed (Open with…, Send
    // to, 7-Zip, Git GUI, etc.), and an "Open file location" entry inside a window that *is*
    // showing the folder content is redundant.
    // -----------------------------------------------------------------------------------------

    private void OnItemRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WormholeItemViewModel vm) return;
        var screen = PointToScreen(e.GetPosition(this));
        AresToys.App.Services.Shell.ShellContextMenu.Show(vm.AbsolutePath, this, screen);
        e.Handled = true;
    }

    /// <summary>Keyboard shortcuts on the items list: Del / Shift+Del recycle or permanently
    /// delete the selected files; Ctrl+C / Ctrl+X copy or cut them onto the Windows clipboard
    /// in Explorer-compatible CF_HDROP format with a "Preferred DropEffect" hint; Ctrl+V pastes
    /// a previously copied/cut file selection into the wormhole's source folder via SHFile-
    /// Operation (which surfaces the standard shell prompts for name collisions). Locked
    /// wormholes refuse the mutating gestures (Cut / Paste / Delete) but still allow Copy.</summary>
    /// <summary>Track whether the next <see cref="OnItemsHostSelectionChanged"/> fire was caused
    /// by us programmatically clearing the selection (manager fanning out the "your sibling
    /// took the selection" signal). Without this guard, every clear would recursively trigger
    /// another fan-out → infinite loop / stack overflow.</summary>
    private bool _suppressSelectionBroadcast;

    /// <summary>Public hook used by the manager to clear this wormhole's item selection when a
    /// sibling wormhole picks up a new selection. UnselectAll fires <see cref="OnItemsHostSelectionChanged"/>
    /// internally, so we set the suppress flag first to keep the SelectionChanged handler from
    /// rebroadcasting and bouncing the gesture around the live wormholes.</summary>
    internal void ClearItemSelection()
    {
        if (ItemsHost.SelectedItems.Count == 0) return;
        _suppressSelectionBroadcast = true;
        try { ItemsHost.UnselectAll(); }
        finally { _suppressSelectionBroadcast = false; }
    }

    /// <summary>Single-wormhole selection model — Explorer-style. Whenever this listbox picks up
    /// new items (AddedItems &gt; 0), tell the manager to clear every sibling wormhole's selection
    /// so the user only ever sees one wormhole's tiles highlighted at a time. Programmatic clears
    /// land here too (AddedItems empty / RemovedItems populated), but they don't broadcast.</summary>
    private void OnItemsHostSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSelectionBroadcast) return;
        if (e.AddedItems.Count == 0) return;
        _manager?.NotifyItemSelectionTaken(this);
    }

    private void OnItemsHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift;

        if (e.Key == Key.Delete)
        {
            if (_record.IsLocked) return;
            var paths = SelectedItemPaths();
            if (paths.Length == 0) return;
            // Shift+Del = permanent (no recycle bin). FOF_WANTNUKEWARNING surfaces the
            // standard shell "permanently delete?" prompt. Without Shift, FOF_ALLOWUNDO
            // sends to the recycle bin silently — same gesture Explorer uses, so muscle
            // memory works.
            SendPathsToRecycleBin(paths, permanent: shift);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C)
        {
            var paths = SelectedItemPaths();
            if (paths.Length == 0) return;
            SetClipboardFiles(paths, cut: false);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.X)
        {
            if (_record.IsLocked) return;
            var paths = SelectedItemPaths();
            if (paths.Length == 0) return;
            SetClipboardFiles(paths, cut: true);
            MarkPathsAsCut(paths);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            if (_record.IsLocked) return;
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            if (_record.IsLocked) return;
            var selected = ItemsHost.SelectedItems.OfType<WormholeItemViewModel>().ToList();
            if (selected.Count != 1) return;
            BeginItemRename(selected[0]);
            e.Handled = true;
            return;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Inline rename of a single tile (F2)
    //
    // UX mirrors Explorer: F2 on the selected tile pops a TextBox over the label, the basename
    // is selected (extension untouched, so backspace-then-type behaves like Explorer's "rename
    // the name part"), Enter commits and Esc cancels. Commit performs File.Move / Directory.Move
    // on the underlying path; the parent FolderWatcher's debounce picks up the rename and
    // refreshes the tiles automatically. The overlay TextBox lives in ContentGrid as a sibling
    // of ItemsHost so we can absolutely position it via Margin without inflating the per-item
    // DataTemplate (one shared editor used by whichever tile is being renamed).
    // -----------------------------------------------------------------------------------------

    /// <summary>Tile whose label is currently being edited via <see cref="ItemRenameEditor"/>;
    /// null when no rename is in flight. Captured at BeginItemRename time so Commit knows which
    /// path to move even if the selection moves on (e.g. user clicks elsewhere → LostFocus
    /// commits against the original tile, not the new selection).</summary>
    private WormholeItemViewModel? _renamingItem;

    private void BeginItemRename(WormholeItemViewModel vm)
    {
        // Force container realization for items that aren't in view yet — virtualization can
        // hand us null for off-screen tiles, but ListBox.SelectedItem can't be off-screen in
        // practice (the user just pressed F2 on a visible selection) so the second attempt
        // after UpdateLayout is paranoia.
        var container = ItemsHost.ItemContainerGenerator.ContainerFromItem(vm) as System.Windows.Controls.ListBoxItem;
        if (container is null)
        {
            ItemsHost.UpdateLayout();
            container = ItemsHost.ItemContainerGenerator.ContainerFromItem(vm) as System.Windows.Controls.ListBoxItem;
            if (container is null) return;
        }

        // Position the editor over the label area of the tile. The label sits below the icon
        // in the tile's Grid (row 1), starting at IconSizePx + iconMargin.Top inside the tile.
        // TranslatePoint gives us the tile's top-left in ContentGrid coordinates; we add the
        // label offset to land on the right line.
        var topLeft = container.TranslatePoint(new Point(0, 0), ContentGrid);
        var labelTopOffset = vm.IconSizePx + vm.IconMargin.Top + vm.IconMargin.Bottom;
        ItemRenameEditor.Margin = new Thickness(topLeft.X, topLeft.Y + labelTopOffset, 0, 0);
        ItemRenameEditor.Width = vm.TileWidth;

        var fileName = Path.GetFileName(vm.AbsolutePath);
        ItemRenameEditor.Text = fileName;
        ItemRenameEditor.Visibility = Visibility.Visible;
        _renamingItem = vm;

        // Focus + selection must run after layout so the TextBox is fully realized; otherwise
        // SelectAll/Select silently no-ops on a not-yet-arranged element.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ItemRenameEditor.Focus();
            Keyboard.Focus(ItemRenameEditor);
            // Select the basename only (everything up to the last '.'). For .lnk / .url whose
            // extensions were stripped from DisplayName we still operate on the real filename
            // here, so the user sees the .lnk extension exposed during rename — matching
            // Explorer behaviour with "Hide extensions for known file types" turned off.
            var name = ItemRenameEditor.Text ?? string.Empty;
            var ext = Path.GetExtension(name);
            var baseLen = name.Length - ext.Length;
            if (baseLen <= 0) baseLen = name.Length;
            ItemRenameEditor.Select(0, baseLen);
        }), DispatcherPriority.Loaded);
    }

    private void CommitItemRename()
    {
        var vm = _renamingItem;
        if (vm is null) { EndItemRename(); return; }

        var newName = (ItemRenameEditor.Text ?? string.Empty).Trim();
        EndItemRename();

        if (string.IsNullOrEmpty(newName)) return;
        var oldName = Path.GetFileName(vm.AbsolutePath);
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this,
                "The name contains characters that aren't allowed in filenames.",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dir = Path.GetDirectoryName(vm.AbsolutePath);
        if (string.IsNullOrEmpty(dir)) return;
        var newPath = Path.Combine(dir, newName);

        try
        {
            if (Directory.Exists(vm.AbsolutePath))
                Directory.Move(vm.AbsolutePath, newPath);
            else
                File.Move(vm.AbsolutePath, newPath);
            // FolderWatcher in the manager will tick a Refresh; no manual refresh needed here.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Rename failed: {ex.Message}",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EndItemRename()
    {
        ItemRenameEditor.Visibility = Visibility.Collapsed;
        _renamingItem = null;
    }

    private void OnItemRenameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitItemRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndItemRename(); e.Handled = true; }
    }

    private void OnItemRenameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (ItemRenameEditor.Visibility == Visibility.Visible) CommitItemRename();
    }

    /// <summary>Snapshot of the selected items' absolute paths, defensively filtered to
    /// non-empty strings. Multiple call sites (Del / Copy / Cut handlers) — extracted to keep
    /// each branch a one-liner.</summary>
    private string[] SelectedItemPaths() => ItemsHost.SelectedItems
        .OfType<WormholeItemViewModel>()
        .Select(vm => vm.AbsolutePath)
        .Where(p => !string.IsNullOrEmpty(p))
        .ToArray();

    /// <summary>Mark a batch of paths as "in cut state" — their tiles render at 50 % opacity
    /// (binding driven by <see cref="WormholeItemViewModel.IsCutMarked"/>). Also stamps the
    /// paths into <see cref="_cutPaths"/> so a refresh-driven VM rebuild can re-apply the
    /// flag. Clears any previously-marked items first so a new Ctrl+X supersedes the old
    /// selection — Explorer behaves the same way.</summary>
    private void MarkPathsAsCut(string[] paths)
    {
        // Drop the previous cut set first so a fresh Ctrl+X doesn't leave stale fade-out on
        // items from the last gesture.
        foreach (var vm in _items)
        {
            if (vm.IsCutMarked) vm.IsCutMarked = false;
        }
        _cutPaths.Clear();
        foreach (var p in paths) _cutPaths.Add(p);
        foreach (var vm in _items)
        {
            if (_cutPaths.Contains(vm.AbsolutePath)) vm.IsCutMarked = true;
        }
    }

    /// <summary>Drop the cut state on every tile and forget the cached path set. Called when
    /// the clipboard no longer carries our paths (paste happened somewhere, or another app
    /// took over the clipboard).</summary>
    private void ClearCutState()
    {
        if (_cutPaths.Count == 0) return;
        _cutPaths.Clear();
        foreach (var vm in _items)
        {
            if (vm.IsCutMarked) vm.IsCutMarked = false;
        }
    }

    /// <summary>Re-apply <see cref="_cutPaths"/> against the current <see cref="_items"/> set
    /// after the FileSystemWatcher-driven rebuild. The new VM instances start with IsCutMarked
    /// = false; we flip them back to true for any path still in the cut set. Called from
    /// RebuildItems / RefreshPortalItems after the collection has been repopulated.</summary>
    internal void ReapplyCutMarks()
    {
        if (_cutPaths.Count == 0) return;
        foreach (var vm in _items)
        {
            if (_cutPaths.Contains(vm.AbsolutePath) && !vm.IsCutMarked)
                vm.IsCutMarked = true;
        }
    }

    /// <summary>WM_CLIPBOARDUPDATE arrives whenever the clipboard's owner changes. If the new
    /// payload no longer matches the FileDrop list we wrote (Ctrl+X session is over —
    /// pasted, replaced, or someone else copied something), clear the cut tint. The check
    /// reads the live clipboard once and compares against our cached path set; mismatch =
    /// drop the marks.</summary>
    private IntPtr WindowProcClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        if (_cutPaths.Count == 0) return IntPtr.Zero;
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                ClearCutState();
                return IntPtr.Zero;
            }
            var list = System.Windows.Clipboard.GetFileDropList();
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list) if (p is not null) live.Add(p);
            // Mismatch on count or content → our session is over.
            if (live.Count != _cutPaths.Count || !live.SetEquals(_cutPaths))
            {
                ClearCutState();
            }
        }
        catch
        {
            // Clipboard race / locked by another app — be conservative and drop the marks.
            // Worst case the user sees the cut highlight disappear once; better than a stale
            // fade that never recovers.
            ClearCutState();
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>Push a list of filesystem paths onto the Windows clipboard in the standard
    /// CF_HDROP format Explorer also writes. The <c>Preferred DropEffect</c> shell-clipboard
    /// format is a 4-byte LE DWORD: <c>2</c> = Move (Cut), <c>5</c> = Copy. Setting it lets
    /// the user paste into Explorer / any other app that honours the convention; the receiving
    /// side reads the same bytes to know whether to move or copy. Without it Explorer defaults
    /// to Copy regardless of how we got the items onto the clipboard.</summary>
    private static void SetClipboardFiles(string[] paths, bool cut)
    {
        var data = new System.Windows.DataObject();
        var sc = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths) sc.Add(p);
        data.SetFileDropList(sc);

        // DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2 (Win32 OLE convention; the older Explorer
        // shell wrote 5 for Copy = COPY|LINK but every modern reader accepts the plain 1).
        var effect = new byte[] { (byte)(cut ? 2 : 1), 0, 0, 0 };
        var ms = new MemoryStream(effect);
        data.SetData("Preferred DropEffect", ms);

        System.Windows.Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>Read the clipboard for a CF_HDROP payload + Preferred DropEffect hint, then
    /// run a shell move-or-copy into the wormhole's source folder. SHFileOperation handles
    /// the conflict-rename prompt + progress UI automatically, mirroring what Explorer does
    /// when pasting in a folder. On a successful Cut paste we clear the clipboard to match
    /// Explorer's behaviour (the cut source disappears from the clipboard after the move).</summary>
    private void PasteFromClipboard()
    {
        if (_record.Portal?.SourcePath is not { } dest) return;
        if (!Directory.Exists(dest)) return;
        if (!System.Windows.Clipboard.ContainsFileDropList()) return;

        var fileList = System.Windows.Clipboard.GetFileDropList();
        if (fileList.Count == 0) return;
        var paths = new string[fileList.Count];
        for (var i = 0; i < fileList.Count; i++) paths[i] = fileList[i]!;

        // Sniff the Preferred DropEffect hint to distinguish Cut from Copy. Default to Copy if
        // the source didn't write the hint (some apps put a CF_HDROP without it).
        var isCut = false;
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data?.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var bytes = new byte[4];
                _ = ms.Read(bytes, 0, 4);
                // DROPEFFECT_MOVE = 2; treat anything else as Copy.
                isCut = bytes[0] == 2;
            }
        }
        catch { /* clipboard race — ignore, fall back to Copy */ }

        ShellCopyOrMove(paths, dest, move: isCut);

        if (isCut)
        {
            // Explorer clears the clipboard after a Cut+Paste so a stray Ctrl+V can't move
            // the same files twice. Mirror that behaviour.
            try { System.Windows.Clipboard.Clear(); } catch { }
        }
    }

    // -----------------------------------------------------------------------------------------
    // SHFileOperation P/Invoke — used by the keyboard shortcuts (Del / Ctrl+V) to recycle,
    // permanently delete, copy, and move files with the same shell prompts + progress UI
    // Explorer surfaces.
    // -----------------------------------------------------------------------------------------

    // pFrom / pTo are IntPtr (not C# string) because the LPWStr marshaller would truncate the
    // wide-string at the first embedded NUL — which is exactly the byte SHFileOperation uses
    // to separate paths in the multi-file list AND to mark the end of the list (double NUL).
    // String marshalling delivers the kernel a buffer with just the first path's content
    // followed by a single NUL, after which the shell scans into uninitialised memory looking
    // for the second NUL terminator → reliable 0xC0000005 access violation. Manual HGlobal
    // allocation of the full double-NUL-terminated buffer is the documented workaround.
    //
    // No `Pack = 1` here: older pinvoke.net snippets carry the attribute over from the 32-bit
    // era when shellapi.h shipped the struct inside a #pragma pack(1) block, but on x64 the
    // IntPtr / pointer fields require natural 8-byte alignment — forcing 1-byte packing makes
    // SHFileOperation read pFrom from a misaligned address and access-violate. The CLR's
    // default sequential layout naturally aligns each field on its size, which matches the
    // shellapi.h struct on modern 64-bit builds.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }
    private const uint FO_MOVE   = 0x0001;
    private const uint FO_COPY   = 0x0002;
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO        = 0x0040;
    private const ushort FOF_NOCONFIRMATION   = 0x0010;
    private const ushort FOF_WANTNUKEWARNING  = 0x4000;
    private const ushort FOF_MULTIDESTFILES   = 0x0001;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    // -----------------------------------------------------------------------------------------
    // IShellLink / IPersistFile — used by the right-button drag "Create shortcut here" path.
    // Two COM interfaces give us everything we need: IShellLinkW.SetPath fills the target,
    // IPersistFile.Save writes the .lnk to disk. No third-party deps, ~25 lines of marshalling.
    // -----------------------------------------------------------------------------------------

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Declared in vtable order; only SetPath / SetIconLocation are called below but the
        // earlier slots must be present so the indices line up. Using IntPtr for unused
        // out-parameter buffers keeps the marshalling cheap and self-contained.
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("0000010B-0000-0000-C000-000000000046")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [System.Runtime.InteropServices.PreserveSig] int IsDirty();
        void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>Write a Windows .lnk shortcut at <paramref name="shortcutPath"/> pointing at
    /// <paramref name="targetPath"/>. Mirrors what Explorer does when the user picks
    /// "Create shortcut here" from the right-button drag menu — same icon, same working
    /// directory (the target's parent).</summary>
    private static void CreateShellShortcut(string targetPath, string shortcutPath)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(targetPath);
            var workingDir = System.IO.Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
            // Inherit the target's icon — Explorer's default for .lnk creation. Index 0 picks
            // the first icon from the target's icon resource (or the file-type association for
            // non-PE files / folders).
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Allocate an unmanaged double-NUL-terminated UTF-16 path list in the format
    /// SHFileOperation expects: <c>path1\0path2\0…\0pathN\0\0</c>. Caller MUST free with
    /// <see cref="System.Runtime.InteropServices.Marshal.FreeHGlobal"/>. Pre-computes the total
    /// char count so the buffer size is exact (no overallocation).</summary>
    private static IntPtr AllocDoubleNullTerminatedList(string[] paths)
    {
        var totalChars = 1; // final terminating NUL
        foreach (var p in paths) totalChars += p.Length + 1;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(totalChars * sizeof(char));
        var offset = 0;
        foreach (var p in paths)
        {
            for (var i = 0; i < p.Length; i++)
            {
                System.Runtime.InteropServices.Marshal.WriteInt16(buffer, offset, (short)p[i]);
                offset += sizeof(char);
            }
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, offset, 0); // per-path NUL
            offset += sizeof(char);
        }
        System.Runtime.InteropServices.Marshal.WriteInt16(buffer, offset, 0); // final NUL (the "double null" marker)
        return buffer;
    }

    /// <summary>Send one or more paths to the Recycle Bin (or permanently delete when
    /// <paramref name="permanent"/> is true). Multi-select supported via the double-NUL path
    /// list format SHFileOperation expects.</summary>
    private static void SendPathsToRecycleBin(string[] paths, bool permanent)
    {
        if (paths.Length == 0) return;
        var pFrom = AllocDoubleNullTerminatedList(paths);
        try
        {
            var flags = permanent
                ? (ushort)FOF_WANTNUKEWARNING                       // user gets the shell's permanent-delete prompt
                : (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION);     // silent recycle, recoverable
            var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = pFrom, fFlags = flags };
            // Return is non-zero on shell-side error / user-cancel — irrelevant here, the shell
            // already surfaced whatever message it wanted to. Discard explicitly to silence the
            // CA1806 analyzer.
            _ = SHFileOperation(ref op);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pFrom); }
    }

    /// <summary>Move-or-copy a batch of paths into a destination directory. The shell's
    /// own progress + conflict UI takes over from here — no custom AresToys dialog needed,
    /// the experience matches what the user gets in Explorer.</summary>
    private static void ShellCopyOrMove(string[] paths, string destFolder, bool move)
    {
        if (paths.Length == 0) return;
        var pFrom = AllocDoubleNullTerminatedList(paths);
        var pTo   = AllocDoubleNullTerminatedList(new[] { destFolder });
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = move ? FO_MOVE : FO_COPY,
                pFrom = pFrom,
                pTo   = pTo,
                fFlags = FOF_ALLOWUNDO,
            };
            _ = SHFileOperation(ref op);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pFrom);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pTo);
        }
    }

    /// <summary>Launch the item via shell verb — the default verb (Open) for the file type.
    /// Wired to the double-click handler on each tile; never called from the right-click flow
    /// anymore (which goes straight to the shell context menu and lets the user pick Open
    /// themselves).</summary>
    private void OpenItem(WormholeItemViewModel vm)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = vm.AbsolutePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerForDialogs(),"Couldn't open the item:\n" + ex.Message,
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Roll / lock state
    // -----------------------------------------------------------------------------------------

    private void ToggleRoll()
    {
        _record.IsRolled = !_record.IsRolled;
        ApplyRollState();
        _onPersist();
    }

    private void ApplyRollState()
    {
        if (_record.IsRolled)
        {
            ContentArea.Visibility = Visibility.Collapsed;
            // Window.MinHeight (80 in the XAML) silently clamps the rolled Height we set
            // below — without dropping MinHeight first, the rolled wormhole stayed 80 px tall
            // and showed ~46 px of body backdrop strip under the header. Drop it to 0 while
            // rolled, restore on unroll.
            //
            // Target collapsed size: 32 (header) + 1+1 borders + ~14 strip ≈ 48 px. The thin
            // strip is intentional — user-requested "circa il 50% dell'header" — to keep the
            // wormhole feeling like an object that can be unrolled, not just the title bar.
            MinHeight = 0;
            Height = 48;
            ResizeMode = ResizeMode.NoResize;
            ChevronGlyph.Text = ChevronDownGlyph;
        }
        else
        {
            ContentArea.Visibility = Visibility.Visible;
            // Order matters: set Height FIRST while MinHeight is still 0 (from the previous
            // rolled state). Setting MinHeight=80 first would leave a pending "clamp Height up
            // to 80" pass that interleaves badly with our explicit Height=UnrolledHeight
            // assignment — the user reported the wormhole staying ~80 px tall instead of
            // restoring to the saved height. Once Height matches UnrolledHeight (>= 80) we can
            // safely raise MinHeight back to 80 without re-clamping.
            Height = _record.Geometry.UnrolledHeight;
            MinHeight = 80;
            ResizeMode = _record.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
            ChevronGlyph.Text = ChevronUpGlyph;
        }
    }

    private void ApplyLockState()
    {
        LockGlyph.Text = _record.IsLocked ? LockClosedGlyph : LockOpenGlyph;
        if (!_record.IsRolled)
            ResizeMode = _record.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
    }
}
