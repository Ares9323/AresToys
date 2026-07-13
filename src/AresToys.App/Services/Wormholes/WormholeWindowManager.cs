using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using AresToys.App.Services.Launcher;
using AresToys.App.Views;

namespace AresToys.App.Services.Wormholes;

public sealed class WormholeWindowManager : IWormholeWindowManager
{
    private readonly IWormholeStore _store;
    private readonly IconService _icons;
    private readonly DesktopLayerHost _desktopLayer;
    private readonly WormholeDefaultsService _defaults;
    private readonly Favicons.FaviconService _favicons;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WormholeWindowManager> _logger;
    private readonly Dictionary<Guid, WormholeWindow> _live = new();
    private readonly Dictionary<Guid, FolderWatcher> _watchers = new();
    private bool _initialized;

    /// <summary>Debounce timer for <see cref="OnDisplaySettingsChanged"/>. An RDP connect /
    /// disconnect or a resolution swap fires DisplaySettingsChanged several times in a burst;
    /// we restart this on each and only switch the active monitor-setup once the burst has
    /// been quiet for the interval. Created lazily on the UI thread (the handler marshals
    /// there) so the timer is bound to the dispatcher the wormhole windows live on.</summary>
    private DispatcherTimer? _displaySettleTimer;

    /// <summary>Monitor fingerprint the live layout currently reflects. Set once at startup and
    /// updated only when a display settle detects a DIFFERENT fingerprint, so a spurious display
    /// event (lock/unlock, DPI tick) on the same setup does NOT re-apply the preset and wipe the
    /// user's un-saved live moves. A genuine setup change re-applies the mapped preset.</summary>
    private string _lastSetupHash = string.Empty;

    public WormholeWindowManager(
        IWormholeStore store,
        IconService icons,
        DesktopLayerHost desktopLayer,
        WormholeDefaultsService defaults,
        Favicons.FaviconService favicons,
        ILoggerFactory loggerFactory,
        ILogger<WormholeWindowManager> logger)
    {
        _store = store;
        _icons = icons;
        _desktopLayer = desktopLayer;
        _defaults = defaults;
        _favicons = favicons;
        _loggerFactory = loggerFactory;
        _logger = logger;
        // Two separate paths so the cheap slider (opacity) doesn't pay the expensive rebuild
        // (icons re-extracted via IShellItemImageFactory) the icon-size knob requires.
        // Opacity drag was reported as laggy — fanning out RebuildItems on every slider tick
        // was the culprit; only the backdrop opacity needs to refresh for that path.
        _defaults.OpacityChanged       += (_, _) => RefreshAllLiveOpacity();
        _defaults.BorderOpacityChanged += (_, _) => RefreshAllLiveOpacity();
        _defaults.IconSizeChanged    += (_, _) => RefreshAllLiveIconSize();
        // TilePaddingChanged → rebuild items so the new TileWidth/TileHeight take effect. Icon
        // cache is keyed on (path,size) and size didn't change, so re-extract is a no-op cache
        // hit — much cheaper than the icon-size path.
        _defaults.TilePaddingChanged += (_, _) => RefreshAllLiveIconSize();
        // LineSpacingChanged, LabelFontSizeChanged, LabelMaxLinesChanged all rebuild item VMs
        // (no icon re-extraction — cached, since IconSize itself didn't change).
        _defaults.LineSpacingChanged    += (_, _) => RefreshAllLiveIconSize();
        _defaults.LabelFontSizeChanged  += (_, _) => RefreshAllLiveIconSize();
        _defaults.LabelMaxLinesChanged  += (_, _) => RefreshAllLiveIconSize();
        // Toggling web-link favicons rebuilds item lists: ON kicks off favicon fetches for every
        // live .url, OFF just stops new fetches (already-stamped .url files keep their icon).
        _defaults.WebLinkFaviconsChanged += (_, _) => RefreshAllLiveIconSize();

        // React to resolution / monitor / RDP display changes so Windows' automatic rescue of
        // off-screen top-level windows doesn't corrupt the saved wormhole layout. App-lifetime
        // singleton, so the subscription naturally lasts until process exit — no unsubscribe.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // Also listen for session changes (RDP connect/disconnect, console attach/detach, unlock).
        // These fire EARLIER in the RDP handshake than the WM_DISPLAYCHANGE that
        // DisplaySettingsChanged rides on — often before Windows has rescued the wormholes onto
        // the smaller remote desktop — so arming the freeze here catches the window where the
        // display event is still silent and rescue coordinates would otherwise be persisted into
        // the physical setup's positions file, corrupting the home layout (the "RDP scrambles the
        // wormholes and they never come back" report). See OnSessionSwitch.
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Fired (on the SystemEvents helper thread) when the display configuration changes:
    /// resolution swap, monitor hot-plug/disconnect, or an RDP session attaching a different-sized
    /// screen. Freezes geometry persistence immediately so the cascade of LocationChanged events
    /// Windows raises while rescuing off-screen windows can't overwrite the user's layout, then
    /// arms a debounce; once the display has been quiet for the interval we hand the new monitor
    /// configuration to the store, which loads / clones the per-setup layout and returns the
    /// snapshot we push onto the live windows (see <see cref="OnDisplaySettleTickAsync"/>).</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ArmDisplaySettleFreeze();

    /// <summary>Fired on RDP connect/disconnect, console attach/detach and session lock/unlock.
    /// These bracket the monitor-topology change and, crucially, land BEFORE the
    /// WM_DISPLAYCHANGE that <see cref="OnDisplaySettingsChanged"/> depends on can fire — so on an
    /// RDP handshake we get the geometry-persist freeze armed while Windows is still moving the
    /// wormholes onto the remote desktop, instead of after (by which point the rescue coordinates
    /// have already been written into the active positions file). Only the reasons that actually
    /// swap the display are handled; SessionLogon / SessionLogoff etc. don't touch the topology.
    /// The settle timer (re-armed on each subsequent DisplaySettingsChanged in the burst) does the
    /// real hash recompute + per-setup switch once the display stops moving.</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.SessionUnlock:
                ArmDisplaySettleFreeze();
                break;
        }
    }

    /// <summary>Freeze geometry persistence and (re)arm the settle-debounce timer. Shared by the
    /// display-settings and session-switch handlers. Marshals to the UI thread because both
    /// SystemEvents callbacks arrive on a helper thread while the timer + windows live on the
    /// dispatcher. Idempotent: re-arming while already frozen just restarts the debounce.</summary>
    private void ArmDisplaySettleFreeze()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            // Freeze geometry persistence DURING the burst — Windows yanks off-screen top-level
            // windows back onto the visible area on resolution/monitor changes and fires
            // LocationChanged per rescue. Without the freeze every rescue would persist into
            // whichever positions file is currently active, corrupting the layout for the
            // PREVIOUS setup (the one we're about to switch away from).
            WormholeWindow.SuppressGeometryPersist = true;
            _displaySettleTimer ??= CreateDisplaySettleTimer();
            _displaySettleTimer.Stop();
            _displaySettleTimer.Start();
        });
    }

    private DispatcherTimer CreateDisplaySettleTimer()
    {
        // 2 s after the LAST display event — long enough for Windows to finish its burst of
        // monitor-arrival / work-area / DPI ticks (an RDP connect fires several back-to-back).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (s, ev) => _ = OnDisplaySettleTickAsync();
        return timer;
    }

    private async Task OnDisplaySettleTickAsync()
    {
        _displaySettleTimer?.Stop();
        try
        {
            var newSetupHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
            if (string.Equals(newSetupHash, _lastSetupHash, StringComparison.Ordinal))
            {
                // Same monitor setup as before (a lock/unlock, DPI tick, or duplicate event).
                // Do NOT re-apply a preset — that would wipe un-saved live moves. Just settle.
                _logger.LogDebug("Wormholes: display settled, setup unchanged ({Hash})", newSetupHash);
                return;
            }

            // Genuine setup change. Apply the preset mapped to the new setup, if any. Unknown
            // setup (e.g. an RDP resolution never saved) → leave the live layout untouched, so a
            // good layout is never overwritten. No clamping.
            await ApplyLayoutForCurrentSetupAsync().ConfigureAwait(true);
            _lastSetupHash = newSetupHash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wormholes: display settle handling failed");
        }
        finally
        {
            WormholeWindow.SuppressGeometryPersist = false;
        }
    }

    /// <summary>Look up the preset mapped to the current monitor fingerprint and, if there is one,
    /// apply it (geometry + hidden/locked/rolled) to the store + live windows. Unknown setup →
    /// no-op. No clamping.</summary>
    private async Task ApplyLayoutForCurrentSetupAsync()
    {
        string? presetName;
        try { presetName = await _store.GetPresetNameForCurrentSetupAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogError(ex, "Wormholes: preset lookup for current setup failed"); return; }

        if (string.IsNullOrEmpty(presetName))
        {
            _logger.LogDebug("Wormholes: current monitor setup has no associated preset; layout left as-is");
            return;
        }

        if (await ApplyPresetToLiveAsync(presetName).ConfigureAwait(true))
            _logger.LogInformation("Wormholes: auto-applied preset '{Name}' for current setup", presetName);
    }

    /// <summary>Apply a preset by name to the records + live windows: push each wormhole's saved
    /// geometry and hidden/locked/rolled state, persist, then reconcile every window (spawn a
    /// newly-visible wormhole, close a newly-hidden one, or refresh geometry + lock/roll in place).
    /// Returns false if the preset doesn't exist. States absent from an older preset leave those
    /// flags untouched. No clamping. Shared by manual Restore and the setup-change auto-apply.</summary>
    private async Task<bool> ApplyPresetToLiveAsync(string name)
    {
        var geom = await _store.GetPresetPositionsAsync(name, CancellationToken.None).ConfigureAwait(true);
        if (geom is null) return false;
        var states = await _store.GetPresetStatesAsync(name, CancellationToken.None).ConfigureAwait(true);

        var records = (await _store.LoadAllAsync(CancellationToken.None).ConfigureAwait(true)).ToList();
        foreach (var rec in records)
        {
            if (geom.TryGetValue(rec.Id, out var g))
            {
                rec.Geometry.X = g.X;
                rec.Geometry.Y = g.Y;
                rec.Geometry.Width = g.Width;
                rec.Geometry.Height = g.Height;
                rec.Geometry.UnrolledHeight = g.UnrolledHeight;
                rec.Geometry.MonitorId = g.MonitorId;
            }
            if (states.TryGetValue(rec.Id, out var s))
            {
                rec.IsHidden = s.Hidden;
                rec.IsLocked = s.Locked;
                rec.IsRolled = s.Rolled;
            }
        }

        try { await _store.FlushAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Wormholes: flush during preset apply failed"); }

        // Reconcile each window to the (possibly changed) record state: spawn/close on hidden
        // flips, push geometry + refresh lock/roll otherwise. Iterate the snapshot list, not the
        // live cache, since ReconcileAsync mutates _live.
        foreach (var rec in records)
        {
            try { await ReconcileAsync(rec, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Wormholes: reconcile during preset apply failed for {Id}", rec.Id); }
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Layout presets (tray + Settings). Save snapshots current geometry + per-wormhole state and
    // binds it to this monitor setup; Restore applies a preset to the live windows and re-binds
    // this setup to it.
    // ------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken cancellationToken)
        => _store.ListPresetNamesAsync(cancellationToken);

    public string PresetsFolderPath => _store.PresetsFolderPath;

    public Task<string?> CurrentSetupPresetAsync(CancellationToken cancellationToken)
        => _store.GetPresetNameForCurrentSetupAsync(cancellationToken);

    public async Task SaveCurrentAsPresetAsync(string name, CancellationToken cancellationToken)
    {
        await _store.SavePresetAsync(name, cancellationToken).ConfigureAwait(true);
        _lastSetupHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
    }

    public async Task DeletePresetAsync(string name, CancellationToken cancellationToken)
        => await _store.DeletePresetAsync(name, cancellationToken).ConfigureAwait(true);

    public Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken)
        => _store.RenamePresetAsync(oldName, newName, cancellationToken);

    public async Task RestorePresetAsync(string name, CancellationToken cancellationToken)
    {
        if (!await ApplyPresetToLiveAsync(name).ConfigureAwait(true)) return;
        await _store.AssociateCurrentSetupAsync(name, cancellationToken).ConfigureAwait(true);
        _lastSetupHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        foreach (var rec in records) RecordChanged?.Invoke(this, rec.Id);
    }

    /// <summary>See <see cref="IWormholeWindowManager.NotifyItemSelectionTaken"/>. Iterates the
    /// live windows and asks every one EXCEPT the source to clear its ListBox selection. Cheap
    /// (UnselectAll is O(N) over visible items) and short-circuits on the common case of "only
    /// one wormhole open" (no other windows to touch). Also fires <see cref="WormholeFocused"/>
    /// because if the user just clicked an item, they've focused that wormhole.</summary>
    public void NotifyItemSelectionTaken(System.Windows.Window source)
    {
        foreach (var (_, window) in _live)
        {
            if (ReferenceEquals(window, source)) continue;
            try { window.ClearItemSelection(); }
            catch (Exception ex) { _logger.LogWarning(ex, "ClearItemSelection failed on sibling wormhole"); }
        }
        NotifyWormholeFocused(source);
    }

    public event EventHandler<Guid>? WormholeFocused;

    /// <summary>See <see cref="IWormholeWindowManager.NotifyWormholeFocused"/>. Looks up the
    /// record id for the calling window and fires <see cref="WormholeFocused"/>; subscribers
    /// (Settings panel) use the id to highlight the matching row.</summary>
    public void NotifyWormholeFocused(System.Windows.Window source)
    {
        foreach (var (id, window) in _live)
        {
            if (!ReferenceEquals(window, source)) continue;
            WormholeFocused?.Invoke(this, id);
            return;
        }
    }

    private void RefreshAllLiveOpacity()
    {
        foreach (var (_, window) in _live)
        {
            try { window.RefreshOpacity(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshOpacity failed during defaults change"); }
        }
    }

    private void RefreshAllLiveIconSize()
    {
        foreach (var (_, window) in _live)
        {
            try { window.RefreshIconSize(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshIconSize failed during defaults change"); }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        _initialized = true;

        // Snapshot the records into an independent list — IWormholeStore.LoadAllAsync returns a
        // ReadOnlyCollection wrapping the store's internal cache, and SpawnWindow triggers WPF's
        // initial Show() which clamps the window's position on multi-monitor / PerMonitorV2
        // setups, which fires LocationChanged → PersistChange → SaveAsync → mutation of the
        // underlying cache mid-foreach. Iterating a snapshot makes the iterator immune to that.
        var records = (await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true)).ToList();
        _logger.LogInformation("Hydrated {Count} wormhole record(s)", records.Count);

        // Per-record try/catch so a broken record (e.g. Portal pointing at a deleted folder,
        // unexpected schema drift, WPF window construction throwing) doesn't kill the rest of
        // the loop. The bad record stays in JSON for next restart; the user can delete it from
        // Settings → Wormholes once that lands.
        foreach (var record in records)
        {
            if (record.IsHidden)
            {
                _logger.LogDebug("Skipping hidden wormhole {Id} ({Title})", record.Id, record.Title);
                continue;
            }
            try
            {
                SpawnWindow(record);
                _logger.LogInformation("Spawned wormhole {Id} title={Title} source={Source}",
                    record.Id, record.Title, record.Portal.SourcePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to spawn wormhole {Id} title={Title}",
                    record.Id, record.Title);
            }
        }

        // Favicon cache hygiene, in the background so it never delays window spawn: gather every
        // host referenced by a live .url (including hidden wormholes — their links still count)
        // and purge cached favicons for hosts no longer present, plus any past the refresh age.
        _ = Task.Run(() => PurgeFaviconCache(records), cancellationToken);

        // Record which monitor setup the just-spawned layout reflects. Startup deliberately does
        // NOT auto-apply a preset: the windows spawn from positions.json (where the user left
        // them), and SnapGeometryIfOffscreen already recovers any window whose stored coords are
        // fully off the current virtual screen. A preset is auto-applied only when a genuine
        // setup change is detected at runtime (see OnDisplaySettleTickAsync).
        _lastSetupHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
    }

    /// <summary>Collect the hosts of every <c>.url</c> across all wormhole sources and hand them
    /// to the favicon service for cache cleanup. Fully defensive — a single unreadable folder or
    /// file is skipped, and the whole sweep is wrapped so it can never crash startup.</summary>
    private void PurgeFaviconCache(IReadOnlyList<WormholeRecord> records)
    {
        try
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var source = record.Portal?.SourcePath;
                if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) continue;
                IEnumerable<string> urlFiles;
                try { urlFiles = Directory.EnumerateFiles(source, "*.url"); }
                catch { continue; }
                foreach (var file in urlFiles)
                {
                    var url = Favicons.UrlShortcutFile.ReadUrl(file);
                    if (url is not null && Favicons.FaviconService.TryGetHttpHost(url, out var host))
                        hosts.Add(host);
                }
            }
            _favicons.PurgeUnreferenced(hosts);
            _logger.LogDebug("Favicon cache purge done; {Count} live host(s)", hosts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Favicon cache purge failed");
        }
    }

    public async Task<WormholeRecord> CreateAsync(string title, string sourceFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
            throw new ArgumentException("A wormhole needs a source folder.", nameof(sourceFolder));

        var record = new WormholeRecord
        {
            Id = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(title) ? "Wormhole" : title,
            Portal = new PortalWormholeConfig { SourcePath = sourceFolder.Trim() },
        };

        // Position roughly centred on the primary monitor, then cascade-offset so multiple
        // new wormholes don't stack perfectly on top of each other (the "I made 3 portals but
        // I only see 1" report). Each attempt shifts (32, 32) px; we stop on the first slot
        // that doesn't sit within 32 px of any existing open wormhole, or after 12 attempts
        // (= ~400 px diagonal) at which point we accept overlap rather than walk off-screen.
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        var baseX = (screenW - record.Geometry.Width) / 2;
        var baseY = (screenH - record.Geometry.Height) / 2;
        const double cascadeStep = 32;
        const int maxAttempts = 12;
        var x = baseX;
        var y = baseY;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var collides = _live.Values.Any(w =>
                Math.Abs(w.Left - x) < cascadeStep && Math.Abs(w.Top - y) < cascadeStep);
            if (!collides) break;
            x = baseX + (attempt + 1) * cascadeStep;
            y = baseY + (attempt + 1) * cascadeStep;
        }
        record.Geometry.X = x;
        record.Geometry.Y = y;

        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(true);
        SpawnWindow(record);
        RecordChanged?.Invoke(this, record.Id);
        return record;
    }

    public async Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        if (_watchers.TryGetValue(wormholeId, out var watcher))
        {
            _watchers.Remove(wormholeId);
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "FolderWatcher dispose failed for {Id}", wormholeId); }
        }
        if (_live.TryGetValue(wormholeId, out var window))
        {
            _live.Remove(wormholeId);
            try { window.CloseFromManager(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close wormhole window {Id}", wormholeId); }
        }
        await _store.DeleteAsync(wormholeId, cancellationToken).ConfigureAwait(true);
    }

    public void CloseAll()
    {
        foreach (var (_, watcher) in _watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "FolderWatcher dispose failed during CloseAll"); }
        }
        _watchers.Clear();
        foreach (var (_, window) in _live)
        {
            try { window.CloseFromManager(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close wormhole window during CloseAll"); }
        }
        _live.Clear();
    }

    private void SpawnWindow(WormholeRecord record)
    {
        if (_live.ContainsKey(record.Id)) return;
        // Recover wormholes whose persisted geometry sits off the visible virtual screen — can
        // happen after a monitor disconnect, a DPI-aware coord drift, or (the recent regression)
        // a SetParent that shifted positions out of the visible range. Snap to primary monitor
        // centre and persist the new coords so the next launch is clean.
        if (SnapGeometryIfOffscreen(record))
        {
            _logger.LogWarning("Wormhole {Id} geometry was off-screen ({X}, {Y}); snapped to primary",
                record.Id, record.Geometry.X, record.Geometry.Y);
            _ = _store.SaveAsync(record, CancellationToken.None);
        }

        async void PersistChange()
        {
            try
            {
                await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(true);
                // Notify subscribers (Settings panel) on the UI thread. The Wormholes grid uses
                // this to refresh its X / Y / W / H cells live as the user drags the chrome.
                if (Application.Current is { } a) _ = a.Dispatcher.BeginInvoke(() => RecordChanged?.Invoke(this, record.Id));
                else RecordChanged?.Invoke(this, record.Id);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Wormhole save failed for {Id}", record.Id); }
        }

        var window = new WormholeWindow(
            record,
            PersistChange,
            _icons,
            _store.WormholesRootPath,
            // Shared defaults service (icon size + opacity). The window reads it on every
            // EffectiveIconSize() / ApplyAppearance() call so the slider in Settings →
            // Wormholes propagates live via DefaultsChanged → RefreshAllLiveAppearance above.
            defaults: _defaults,
            // Pass the manager so the window can notify it of item-selection events — the
            // manager fans out a ClearItemSelection() to every sibling wormhole.
            manager: this,
            // Favicon resolver for .url web-link tiles.
            favicons: _favicons);
        window.DeleteRequested += async (_, id) =>
        {
            try { await DeleteAsync(id, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Wormhole delete from menu failed for {Id}", id); }
        };
        _live[record.Id] = window;

        // FolderWatcher pinned to this wormhole's source. The watcher fires Changed events on
        // the dispatcher after a 300 ms quiet period; the window just re-enumerates its source
        // folder each tick. Disposed in DeleteAsync / CloseAll.
        if (record.Portal is { SourcePath: { Length: > 0 } sourcePath })
        {
            var watcher = new FolderWatcher(_loggerFactory.CreateLogger<FolderWatcher>());
            watcher.Changed += (_, _) =>
            {
                if (Application.Current is { } app) app.Dispatcher.BeginInvoke(window.RefreshPortalItems);
                else window.RefreshPortalItems();
            };
            watcher.FullRefreshRequested += (_, _) =>
            {
                if (Application.Current is { } app) app.Dispatcher.BeginInvoke(window.RefreshPortalItems);
                else window.RefreshPortalItems();
            };
            watcher.Start(sourcePath);
            _watchers[record.Id] = watcher;
        }

        // WorkerW / Progman parenting is temporarily disabled — see DesktopLayerHost.cs. The
        // SetParent call succeeds on Win11 24H2+ via the Progman-child strategy, but the
        // coordinate space of the reparented window shifts to the parent's client area which
        // doesn't match WPF's screen-coord Left/Top from the persisted record. Result: every
        // wormhole loads off-screen by the delta between virtual origin and Progman client
        // origin. Until we add proper ScreenToClient conversion + persistence in client coords,
        // wormholes ship as regular top-level WPF windows (not Topmost): they go behind every
        // other app on click, and minimize on Win+D. Trade-off for the v1 — desktop-layer
        // semantics (Win+D reveals, never minimized) come back once the coord conversion lands.
        if (Application.Current is { } current)
            current.Dispatcher.Invoke(() => { window.Show(); window.Activate(); });
        else { window.Show(); window.Activate(); }

        // Diagnostic: log where the window actually ended up vs. what we asked for. On a
        // multi-monitor PerMonitorV2 setup WPF can clamp Left/Top to the nearest visible
        // monitor's work area, or shift by DPI factor — when a user reports "I only see one
        // wormhole" this log nails down whether the others are off-screen, behind something,
        // or simply landed at coordinates the user isn't looking at.
        _logger.LogInformation("Wormhole {Id} window placed at Left={Left} Top={Top} (asked X={X} Y={Y}), Width={W} Height={H}, IsVisible={Visible}, IsActive={Active}",
            record.Id, window.Left, window.Top, record.Geometry.X, record.Geometry.Y,
            window.Width, window.Height, window.IsVisible, window.IsActive);
    }

    /// <summary>If the wormhole's persisted geometry would render mostly off-screen against the
    /// current virtual-screen rect, mutate the record's <see cref="WormholeGeometry"/> to a
    /// safe centred position on the primary monitor. Returns true when a snap occurred so the
    /// caller can persist the corrected geometry. Threshold = at least a small thumbnail
    /// (32×80 px) of the window must be inside the virtual rect; below that we treat the
    /// record as effectively invisible and recover.</summary>
    private static bool SnapGeometryIfOffscreen(WormholeRecord record)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var visLeft = Math.Max(virtualLeft, record.Geometry.X);
        var visTop = Math.Max(virtualTop, record.Geometry.Y);
        var visRight = Math.Min(virtualRight, record.Geometry.X + record.Geometry.Width);
        var visBottom = Math.Min(virtualBottom, record.Geometry.Y + record.Geometry.Height);
        var visW = Math.Max(0, visRight - visLeft);
        var visH = Math.Max(0, visBottom - visTop);
        if (visW * visH >= 32 * 80) return false; // enough of the chrome is visible

        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        record.Geometry.X = Math.Max(20, (screenW - record.Geometry.Width) / 2);
        record.Geometry.Y = Math.Max(20, (screenH - record.Geometry.Height) / 2);
        return true;
    }

    public event EventHandler<Guid>? RecordChanged;

    public Task ReconcileAsync(WormholeRecord record, CancellationToken cancellationToken)
    {
        _live.TryGetValue(record.Id, out var window);
        if (record.IsHidden)
        {
            // Caller already persisted the record; closing the window here matches the chrome
            // hamburger "Hide" path — record survives, window goes away. No-op if there was no
            // live window (e.g. record was already hidden when the user toggled Hidden→Hidden).
            if (window is not null)
            {
                _live.Remove(record.Id);
                if (_watchers.TryGetValue(record.Id, out var w))
                {
                    _watchers.Remove(record.Id);
                    try { w.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Watcher dispose during reconcile failed"); }
                }
                try { window.CloseFromManager(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Window close during reconcile failed"); }
            }
            return Task.CompletedTask;
        }

        // record.IsHidden == false. If we don't already have a live window for it, spawn one;
        // otherwise refresh the existing window's visual state (Lock toggle, Title rename).
        if (window is null)
        {
            try { SpawnWindow(record); }
            catch (Exception ex) { _logger.LogError(ex, "Reconcile spawn failed for {Id}", record.Id); }
            return Task.CompletedTask;
        }
        try
        {
            // Push geometry from record to live window. WPF's LocationChanged + SizeChanged
            // will fire on the resulting Left/Top/Width changes; their handlers persist the
            // same value back to the record (idempotent — saves the value we just wrote). The
            // extra round-trip costs one SaveAsync but keeps the data flow simple (record is
            // always the source of truth; the live window mirrors it).
            if (Math.Abs(window.Left - record.Geometry.X) > 0.5) window.Left = record.Geometry.X;
            if (Math.Abs(window.Top - record.Geometry.Y) > 0.5) window.Top = record.Geometry.Y;
            if (Math.Abs(window.Width - record.Geometry.Width) > 0.5) window.Width = record.Geometry.Width;
            if (!record.IsRolled && Math.Abs(window.Height - record.Geometry.Height) > 0.5)
                window.Height = record.Geometry.Height;
            window.RefreshFromRecord();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Window refresh during reconcile failed"); }
        return Task.CompletedTask;
    }

    /// <summary>Reposition every live wormhole onto the primary monitor in a cascade, then
    /// persist the new geometry. Surfaced through the tray menu so the user has a one-click
    /// recovery when wormholes end up off-screen (monitor disconnect, weird DPI scaling, layout
    /// rearrange between sessions). Each new position is also Activate()d to guarantee it ends
    /// up visible Z-order-wise.</summary>
    public async Task SetAllHiddenAsync(bool hidden, CancellationToken cancellationToken)
    {
        var records = (await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true)).ToList();
        var changed = records.Where(r => r.IsHidden != hidden).ToList();
        if (changed.Count == 0) return;

        // Phase 1 — mutate every record's flag in memory. No I/O yet.
        foreach (var r in changed) r.IsHidden = hidden;

        // Phase 2 — tight UI-thread loop to spawn / close windows. SpawnWindow is essentially
        // "new WormholeWindow + Show()"; Show() is non-blocking (queues a render), so the loop
        // returns in microseconds and WPF batch-renders the lot in the same dispatcher tick
        // instead of the gradient cascade you'd get awaiting ReconcileAsync per-record.
        foreach (var r in changed)
        {
            if (r.IsHidden)
            {
                if (_live.TryGetValue(r.Id, out var w))
                {
                    _live.Remove(r.Id);
                    if (_watchers.Remove(r.Id, out var watch))
                    {
                        try { watch.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Watcher dispose batch failed for {Id}", r.Id); }
                    }
                    try { w.CloseFromManager(); } catch (Exception ex) { _logger.LogWarning(ex, "CloseFromManager batch failed for {Id}", r.Id); }
                }
            }
            else
            {
                if (!_live.ContainsKey(r.Id))
                {
                    try { SpawnWindow(r); } catch (Exception ex) { _logger.LogWarning(ex, "SpawnWindow batch failed for {Id}", r.Id); }
                }
            }
        }

        // Phase 3 — one bulk JSON flush instead of N. The user sees the visual change already;
        // persistence catches up asynchronously without holding up the spawn loop.
        await _store.FlushAsync(cancellationToken).ConfigureAwait(true);
        foreach (var r in changed) RecordChanged?.Invoke(this, r.Id);
    }

    public async Task SetAllLockedAsync(bool locked, CancellationToken cancellationToken)
    {
        var records = (await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true)).ToList();
        var changed = records.Where(r => r.IsLocked != locked).ToList();
        if (changed.Count == 0) return;
        foreach (var r in changed) r.IsLocked = locked;
        foreach (var r in changed)
        {
            if (_live.TryGetValue(r.Id, out var w))
            {
                try { w.RefreshFromRecord(); } catch (Exception ex) { _logger.LogWarning(ex, "Refresh batch failed for {Id}", r.Id); }
            }
        }
        await _store.FlushAsync(cancellationToken).ConfigureAwait(true);
        foreach (var r in changed) RecordChanged?.Invoke(this, r.Id);
    }

    public async Task SetAllRolledAsync(bool rolled, CancellationToken cancellationToken)
    {
        var records = (await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true)).ToList();
        var changed = records.Where(r => r.IsRolled != rolled).ToList();
        if (changed.Count == 0) return;
        foreach (var r in changed) r.IsRolled = rolled;
        foreach (var r in changed)
        {
            if (_live.TryGetValue(r.Id, out var w))
            {
                try { w.RefreshFromRecord(); } catch (Exception ex) { _logger.LogWarning(ex, "Refresh batch failed for {Id}", r.Id); }
            }
        }
        await _store.FlushAsync(cancellationToken).ConfigureAwait(true);
        foreach (var r in changed) RecordChanged?.Invoke(this, r.Id);
    }

    public async Task ToggleAllHiddenAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        // "Any visible → hide everything" is the natural gesture: pressing the hotkey while at
        // least one wormhole is on-screen reads as "clean up the desktop"; pressing it while
        // they're all hidden reads as "bring them back". Both branches reach a fixed point
        // after one press, so a second press always flips it.
        var anyVisible = records.Any(r => !r.IsHidden);
        await SetAllHiddenAsync(anyVisible, cancellationToken).ConfigureAwait(true);
    }

    public async Task ToggleAllLockedAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        var anyUnlocked = records.Any(r => !r.IsLocked);
        await SetAllLockedAsync(anyUnlocked, cancellationToken).ConfigureAwait(true);
    }

    public async Task ToggleAllRolledAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        var anyUnrolled = records.Any(r => !r.IsRolled);
        await SetAllRolledAsync(anyUnrolled, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetAllTopmostAsync(bool topmost, CancellationToken cancellationToken)
    {
        var records = (await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true)).ToList();
        var changed = records.Where(r => r.IsTopmost != topmost).ToList();
        if (changed.Count == 0) return;
        foreach (var r in changed) r.IsTopmost = topmost;
        foreach (var r in changed)
        {
            if (_live.TryGetValue(r.Id, out var w))
            {
                try { w.RefreshFromRecord(); } catch (Exception ex) { _logger.LogWarning(ex, "Refresh batch failed for {Id}", r.Id); }
            }
        }
        await _store.FlushAsync(cancellationToken).ConfigureAwait(true);
        foreach (var r in changed) RecordChanged?.Invoke(this, r.Id);
    }

    public async Task ToggleAllTopmostAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        // "Any not topmost → make all topmost" mirrors the hide / lock / collapse toggles:
        // pressing the hotkey while at least one wormhole is buried under another window reads
        // as "bring them to the front"; pressing again with everything already topmost lowers
        // them back into normal z-order.
        var anyNotTopmost = records.Any(r => !r.IsTopmost);
        await SetAllTopmostAsync(anyNotTopmost, cancellationToken).ConfigureAwait(true);
    }

    private void RefreshLiveWindowItems(Guid id)
    {
        if (!_live.TryGetValue(id, out var window)) return;
        try { window.RebuildItems(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Live items refresh failed for {Id}", id); }
    }

    public void RecenterAll()
    {
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        const double cascadeStep = 32;
        var i = 0;
        foreach (var (_, window) in _live)
        {
            var width = double.IsNaN(window.Width) ? 320 : window.Width;
            var height = double.IsNaN(window.Height) ? 240 : window.Height;
            var x = Math.Max(20, (screenW - width) / 2 - 100 + i * cascadeStep);
            var y = Math.Max(20, (screenH - height) / 2 - 100 + i * cascadeStep);
            window.Left = x;
            window.Top = y;
            window.Activate();
            i++;
        }
    }
}
