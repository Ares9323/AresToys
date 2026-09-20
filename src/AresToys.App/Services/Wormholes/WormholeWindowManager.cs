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
    private readonly ColorWheelLauncher _colors;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WormholeWindowManager> _logger;
    /// <summary>Wormhole id → the window showing it. In a tab group every member maps to the SAME
    /// window instance, so a lookup by any member's id finds the window that hosts it. Iterate
    /// <see cref="LiveWindows"/> rather than this dictionary's values: a grouped window appears
    /// once per tab here, and applying a window-level operation once per tab is at best wasted
    /// work and at worst wrong (a roll toggled three times).</summary>
    private readonly Dictionary<Guid, WormholeWindow> _live = new();

    /// <summary>Each live window exactly once, however many tabs it hosts.</summary>
    private IEnumerable<WormholeWindow> LiveWindows => _live.Values.Distinct();

    /// <summary>Which wormholes share a window. Loaded once at startup from groups.json and kept
    /// in step as the user merges and detaches tabs.</summary>
    private WormholeGroups _groups = new();

    /// <summary>Every known record by id. A grouped window needs its siblings' records — for the
    /// tab strip and to switch what's on show — and those lookups happen from synchronous UI paths
    /// that can't await the store.</summary>
    private readonly Dictionary<Guid, WormholeRecord> _records = new();
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
        ColorWheelLauncher colors,
        ILoggerFactory loggerFactory,
        ILogger<WormholeWindowManager> logger)
    {
        _store = store;
        _icons = icons;
        _desktopLayer = desktopLayer;
        _defaults = defaults;
        _favicons = favicons;
        _colors = colors;
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
        // Expand-on-hover only re-evaluates whether a collapsed wormhole is currently peeked
        // open — no item rebuild.
        _defaults.ExpandCollapsedOnHoverChanged += (_, _) => RefreshAllLiveCollapsedHover();
        _defaults.KeepVisibleOnShowDesktopChanged += (_, _) => RefreshAllLiveDesktopOwnership();
        // One-click open only needs the tile cursor swapped: the click handlers read the flag when
        // the click happens, so nothing about the items themselves changes.
        _defaults.OpenWithOneClickChanged += (_, _) => RefreshAllLiveItemCursor();
        // The service-file filter decides which entries exist as tiles, so this one re-enumerates.
        _defaults.ServiceFilesVisibilityChanged += (_, _) => RefreshAllLivePortalItems();

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
        // live cache, since ReconcileAsync mutates _live. Marshalled onto the UI thread: wormhole
        // windows have thread affinity (setting Left/Top/Width or RefreshFromRecord off the
        // dispatcher throws), and this path runs on a thread-pool thread when a pipeline task
        // fires it — the menu / Settings Restore already runs on the UI thread. Without the
        // marshal the reconcile threw cross-thread and the live windows silently didn't move.
        await RunOnUiAsync(async () =>
        {
            foreach (var rec in records)
            {
                try { await ReconcileAsync(rec, CancellationToken.None).ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Wormholes: reconcile during preset apply failed for {Id}", rec.Id); }
            }
        }).ConfigureAwait(true);
        return true;
    }

    /// <summary>Run <paramref name="action"/> on the WPF UI thread. Inline (no marshal) when the
    /// caller is already on the dispatcher — the menu / Settings Restore path — so behaviour there
    /// is unchanged; marshalled when invoked from a background thread, e.g. the pipeline executor
    /// running the "Switch wormhole preset" task.</summary>
    private static Task RunOnUiAsync(Func<Task> action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) return action();
        return app.Dispatcher.InvokeAsync(action).Task.Unwrap();
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
        // RecordChanged subscribers (the Wormholes Settings grid) mutate UI-bound state, so fire
        // on the dispatcher — this can run off the UI thread when a pipeline task drives the restore.
        await RunOnUiAsync(() =>
        {
            foreach (var rec in records) RecordChanged?.Invoke(this, rec.Id);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    /// <summary>See <see cref="IWormholeWindowManager.NotifyItemSelectionTaken"/>. Iterates the
    /// live windows and asks every one EXCEPT the source to clear its ListBox selection. Cheap
    /// (UnselectAll is O(N) over visible items) and short-circuits on the common case of "only
    /// one wormhole open" (no other windows to touch). Also fires <see cref="WormholeFocused"/>
    /// because if the user just clicked an item, they've focused that wormhole.</summary>
    public void NotifyItemSelectionTaken(System.Windows.Window source)
    {
        foreach (var window in LiveWindows)
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
            // For a grouped window, the row to highlight is the tab on show, not whichever member
            // happens to come first in the dictionary.
            if (window.ActiveRecord.Id != id) continue;
            WormholeFocused?.Invoke(this, id);
            return;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Source folder recovery
    // ------------------------------------------------------------------------------------------

    /// <summary>See <see cref="IWormholeWindowManager.RequestSourceRecovery"/>.</summary>
    public void RequestSourceRecovery(WormholeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = RecoverSourceAsync(record);
    }

    private async Task RecoverSourceAsync(WormholeRecord record)
    {
        try
        {
            var path = record.Portal?.SourcePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Directory.Exists(path)) { CaptureSourceIdentity(record); return; }

            // 1. Identity lookup — authoritative. The folder proves it's the same one, so we
            //    repoint without asking.
            var resolved = await Task.Run(() => ResolveByIdentity(record)).ConfigureAwait(true);
            if (resolved is not null)
            {
                _logger.LogInformation("Wormhole {Id}: source folder was moved, recovered by file id: '{Old}' → '{New}'",
                    record.Id, path, resolved);
                ApplyNewSource(record, resolved);
                return;
            }

            // 2. Name-based guess — offered, never applied silently: matching by name is not
            //    proof of identity, so the user confirms it from the wormhole's banner.
            var suggestion = await SuggestSourceAsync(record).ConfigureAwait(true);
            if (_live.TryGetValue(record.Id, out var window))
            {
                try { window.ApplySourceSuggestion(suggestion); }
                catch (Exception ex) { _logger.LogWarning(ex, "Applying source suggestion failed for {Id}", record.Id); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source recovery failed for wormhole {Id}", record.Id);
        }
    }

    /// <summary>Resolve a record's source through the stored NTFS identity. Null when nothing was
    /// captured, the volume is absent, or the folder is genuinely gone.</summary>
    private static string? ResolveByIdentity(WormholeRecord record)
    {
        var portal = record.Portal;
        if (portal is null) return null;
        if (!FolderIdentity.TryParseFileId(portal.SourceFileId, out var high, out var low)) return null;
        var identity = new FolderIdentity(portal.SourceVolumeSerial, high, low);
        return FolderIdentityResolver.ResolvePath(identity);
    }

    /// <summary>Name-based suggestion for a missing source, derived from the wormholes whose
    /// folders still resolve.</summary>
    private async Task<string?> SuggestSourceAsync(WormholeRecord record)
    {
        var path = record.Portal?.SourcePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var records = await _store.LoadAllAsync(CancellationToken.None).ConfigureAwait(true);
        var healthy = records
            .Where(r => r.Id != record.Id)
            .Select(r => r.Portal?.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => p!)
            .ToList();
        return SourceRelinkPlanner.Suggest(path, healthy, Directory.Exists);
    }

    /// <summary>See <see cref="IWormholeWindowManager.RelinkSource"/>.</summary>
    public void RelinkSource(WormholeRecord record, string newSourcePath)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(newSourcePath) || !Directory.Exists(newSourcePath)) return;
        ApplyNewSource(record, newSourcePath);
    }

    /// <summary>Write a new source path onto the record: capture the folder's identity, persist,
    /// re-attach the watcher and refresh the live window.</summary>
    private void ApplyNewSource(WormholeRecord record, string newPath)
    {
        if (record.Portal is null) return;
        record.Portal.SourcePath = newPath;
        CaptureSourceIdentity(record);
        _ = PersistAndRefreshAsync(record);
    }

    private async Task PersistAndRefreshAsync(WormholeRecord record)
    {
        try { await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persisting recovered source failed for {Id}", record.Id); }

        RestartWatcher(record);
        if (_live.TryGetValue(record.Id, out var window))
        {
            try { window.RebuildItems(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Refresh after source recovery failed for {Id}", record.Id); }
        }
        RecordChanged?.Invoke(this, record.Id);
    }

    /// <summary>Record the folder's NTFS identity so a later rename / move can be undone by
    /// lookup instead of by asking the user. No-op when the volume has no stable ids (FAT, some
    /// network shares) — the recovery chain then just falls through to the name-based path.</summary>
    /// <param name="persist">False lets a caller batch several captures behind one flush — the
    /// startup pass touches every record at once and would otherwise rewrite the whole JSON file
    /// once per wormhole.</param>
    /// <returns>True when the stored identity actually changed.</returns>
    private bool CaptureSourceIdentity(WormholeRecord record, bool persist = true)
    {
        var portal = record.Portal;
        if (portal is null || string.IsNullOrWhiteSpace(portal.SourcePath)) return false;
        var identity = FolderIdentityResolver.Capture(portal.SourcePath);
        if (identity is not { } id) return false;

        var text = id.ToFileIdString();
        if (portal.SourceVolumeSerial == id.VolumeSerialNumber
            && string.Equals(portal.SourceFileId, text, StringComparison.Ordinal)) return false;

        portal.SourceVolumeSerial = id.VolumeSerialNumber;
        portal.SourceFileId = text;
        if (persist) _ = SafeSaveAsync(record);
        return true;
    }

    private async Task SafeSaveAsync(WormholeRecord record)
    {
        try { await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Saving source identity failed for {Id}", record.Id); }
    }

    /// <summary>Dispose the watcher for a record and start a fresh one on its current source.</summary>
    private void RestartWatcher(WormholeRecord record)
    {
        if (_watchers.Remove(record.Id, out var existing))
        {
            try { existing.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Watcher dispose during restart failed for {Id}", record.Id); }
        }
        if (_watchersPaused) return;
        if (!_live.TryGetValue(record.Id, out var window)) return;
        if (record.Portal is not { SourcePath: { Length: > 0 } source }) return;
        if (!Directory.Exists(source)) return;
        _watchers[record.Id] = CreateWatcher(source, window);
    }

    /// <summary>See <see cref="IWormholeWindowManager.MissingSourcesAsync"/>.</summary>
    public async Task<IReadOnlyList<WormholeRecord>> MissingSourcesAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(true);
        return records
            .Where(r => !string.IsNullOrWhiteSpace(r.Portal?.SourcePath) && !Directory.Exists(r.Portal!.SourcePath))
            .ToList();
    }

    /// <summary>See <see cref="IWormholeWindowManager.RelinkMissingSourcesAsync"/>.</summary>
    public async Task<int> RelinkMissingSourcesAsync(string newBaseFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newBaseFolder) || !Directory.Exists(newBaseFolder)) return 0;
        var missing = await MissingSourcesAsync(cancellationToken).ConfigureAwait(true);
        if (missing.Count == 0) return 0;

        var plan = SourceRelinkPlanner.Plan(
            missing.Select(r => (r.Id, r.Portal!.SourcePath)).ToList(),
            newBaseFolder,
            Directory.Exists);
        if (plan.Count == 0) return 0;

        foreach (var proposal in plan)
        {
            var record = missing.FirstOrDefault(r => r.Id == proposal.Id);
            if (record?.Portal is null) continue;
            record.Portal.SourcePath = proposal.NewPath;
            CaptureSourceIdentity(record);
            RestartWatcher(record);
            if (_live.TryGetValue(record.Id, out var window))
            {
                try { window.RebuildItems(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Refresh after bulk relink failed for {Id}", record.Id); }
            }
            _logger.LogInformation("Wormhole {Id}: source relinked '{Old}' → '{New}'",
                record.Id, proposal.OldPath, proposal.NewPath);
        }

        try { await _store.FlushAsync(cancellationToken).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Flush after bulk relink failed"); }
        foreach (var proposal in plan) RecordChanged?.Invoke(this, proposal.Id);
        return plan.Count;
    }

    // ------------------------------------------------------------------------------------------
    // Watcher pause — lets the user rename / move source folders without closing the app
    // ------------------------------------------------------------------------------------------

    private bool _watchersPaused;

    public bool WatchersPaused => _watchersPaused;

    public event EventHandler? WatchersPausedChanged;

    /// <summary>See <see cref="IWormholeWindowManager.PauseWatchers"/>. Measured behaviour: a
    /// live FileSystemWatcher holds a handle on its folder, and Windows refuses to rename ANY
    /// ancestor of a folder with an open handle inside it — which is exactly what happens when
    /// the user tries to rename the folder that holds all their wormhole sources.</summary>
    public void PauseWatchers()
    {
        if (_watchersPaused) return;
        _watchersPaused = true;
        foreach (var (id, watcher) in _watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Watcher dispose during pause failed for {Id}", id); }
        }
        _watchers.Clear();
        _logger.LogInformation("Wormholes: folder watchers paused ({Count} released)", _live.Count);
        WatchersPausedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>See <see cref="IWormholeWindowManager.ResumeWatchers"/>.</summary>
    public void ResumeWatchers()
    {
        if (!_watchersPaused) return;
        _watchersPaused = false;
        _ = ResumeWatchersAsync();
        WatchersPausedChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ResumeWatchersAsync()
    {
        try
        {
            var records = await _store.LoadAllAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var record in records.ToList())
            {
                if (!_live.ContainsKey(record.Id)) continue;
                // Recover BEFORE re-attaching: the whole point of the pause is that the user was
                // reorganising folders, so the path on record may well be stale now.
                var path = record.Portal?.SourcePath;
                if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
                {
                    var resolved = await Task.Run(() => ResolveByIdentity(record)).ConfigureAwait(true);
                    if (resolved is not null)
                    {
                        _logger.LogInformation("Wormhole {Id}: source recovered after watcher pause: '{Old}' → '{New}'",
                            record.Id, path, resolved);
                        record.Portal!.SourcePath = resolved;
                        CaptureSourceIdentity(record);
                        try { await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(true); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Save after resume recovery failed for {Id}", record.Id); }
                        RecordChanged?.Invoke(this, record.Id);
                    }
                }
                RestartWatcher(record);
                if (_live.TryGetValue(record.Id, out var window))
                {
                    try { window.RebuildItems(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Refresh after watcher resume failed for {Id}", record.Id); }
                }
            }
            _logger.LogInformation("Wormholes: folder watchers resumed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resuming folder watchers failed");
        }
    }

    /// <summary>See <see cref="IWormholeWindowManager.LiveWindowHandles"/>.</summary>
    public IReadOnlyList<IntPtr> LiveWindowHandles()
    {
        var handles = new List<IntPtr>(_live.Count);
        foreach (var window in LiveWindows)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) handles.Add(handle);
        }
        return handles;
    }

    private void RefreshAllLiveOpacity()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshOpacity(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshOpacity failed during defaults change"); }
        }
    }

    private void RefreshAllLiveDesktopOwnership()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshDesktopOwnership(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshDesktopOwnership failed during defaults change"); }
        }
    }

    private void RefreshAllLiveCollapsedHover()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshCollapsedHover(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshCollapsedHover failed during defaults change"); }
        }
    }

    private void RefreshAllLiveIconSize()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshIconSize(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshIconSize failed during defaults change"); }
        }
    }

    private void RefreshAllLiveItemCursor()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshItemCursor(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshItemCursor failed during defaults change"); }
        }
    }

    private void RefreshAllLivePortalItems()
    {
        foreach (var window in LiveWindows)
        {
            try { window.RefreshPortalItems(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RefreshPortalItems failed during defaults change"); }
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

        _records.Clear();
        foreach (var r in records) _records[r.Id] = r;

        // Tab groups come from their own file and may name wormholes that no longer exist; the
        // store prunes those on read, so what lands here is already consistent with the records.
        try
        {
            _groups = await _store.LoadGroupsAsync(cancellationToken).ConfigureAwait(true);
            if (_groups.All.Count > 0)
                _logger.LogInformation("Hydrated {Count} wormhole tab group(s)", _groups.All.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading wormhole tab groups failed — carrying on without them");
            _groups = new WormholeGroups();
        }

        // Per-record try/catch so a broken record (e.g. Portal pointing at a deleted folder,
        // unexpected schema drift, WPF window construction throwing) doesn't kill the rest of
        // the loop. The bad record stays in JSON for next restart; the user can delete it from
        // Settings → Wormholes once that lands.
        // Source recovery runs for EVERY record, hidden ones included: a hidden wormhole whose
        // folder was renamed while the app was closed should come back pointing at the right place
        // the moment the user unhides it. This is also where the folder identity gets backfilled
        // onto records created before the feature existed, which is what makes a FUTURE rename
        // recoverable at all. One flush at the end rather than one save per record.
        var sourcesChanged = false;
        foreach (var record in records) sourcesChanged |= RecoverOrCaptureSourceAtStartup(record);
        if (sourcesChanged)
        {
            try { await _store.FlushAsync(cancellationToken).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Flush of recovered source paths failed"); }
        }

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

    /// <summary>Startup pass over one record's source folder: if the path resolves, record (or
    /// refresh) its NTFS identity; if it doesn't, try to find the folder by that identity and
    /// repoint the record before its window is spawned. Synchronous on purpose — it runs before
    /// the windows exist, and a file-id lookup is a couple of handle opens.</summary>
    /// <returns>True when the record was modified and the caller should flush.</returns>
    private bool RecoverOrCaptureSourceAtStartup(WormholeRecord record)
    {
        try
        {
            var path = record.Portal?.SourcePath;
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (Directory.Exists(path)) return CaptureSourceIdentity(record, persist: false);

            var resolved = ResolveByIdentity(record);
            if (resolved is null)
            {
                _logger.LogInformation("Wormhole {Id}: source folder '{Path}' is missing and couldn't be resolved by id",
                    record.Id, path);
                return false;
            }
            _logger.LogInformation("Wormhole {Id}: source folder moved while the app was closed, recovered by file id: '{Old}' → '{New}'",
                record.Id, path, resolved);
            record.Portal!.SourcePath = resolved;
            CaptureSourceIdentity(record, persist: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup source recovery failed for wormhole {Id}", record.Id);
            return false;
        }
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

        // Capture the folder's identity up front so this wormhole survives a later rename / move
        // of its source without the user having to point at it again.
        CaptureSourceIdentity(record);

        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(true);
        SpawnWindow(record);
        RecordChanged?.Invoke(this, record.Id);
        return record;
    }

    public WormholeGroup? GroupFor(Guid wormholeId) => _groups.FindFor(wormholeId);

    /// <summary>Window currently lit up as a merge target, so the cue can be cleared when the
    /// pointer moves off it or the drag ends.</summary>
    private WormholeWindow? _mergeHintWindow;

    public void HighlightMergeTarget(Guid? wormholeId)
    {
        WormholeWindow? next = null;
        if (wormholeId is { } id) _live.TryGetValue(id, out next);
        if (ReferenceEquals(next, _mergeHintWindow)) return;

        try { _mergeHintWindow?.ShowMergeTargetHint(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Clearing the merge hint failed"); }
        _mergeHintWindow = next;
        try { _mergeHintWindow?.ShowMergeTargetHint(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Showing the merge hint failed"); }
    }

    public void ClearMergeHighlight() => HighlightMergeTarget(null);

    /// <summary>Which wormhole's header sits under a screen point, ignoring <paramref name="exclude"/>
    /// (the one being dragged). Returns the id of the tab currently on show there, so dropping onto
    /// a group joins the group rather than trying to merge with a tab that isn't visible.</summary>
    public Guid? FindHeaderTargetAt(int screenX, int screenY, WormholeWindow exclude)
    {
        foreach (var window in LiveWindows)
        {
            if (ReferenceEquals(window, exclude)) continue;
            try
            {
                if (window.HeaderHitTest(screenX, screenY)) return window.ActiveRecord.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Header hit-test failed while looking for a merge target");
            }
        }
        return null;
    }

    /// <summary>The records behind a wormhole's tabs, in display order. Empty when it isn't
    /// grouped — a lone wormhole has no tab strip to draw.</summary>
    public IReadOnlyList<WormholeRecord> TabsFor(Guid wormholeId)
    {
        var group = _groups.FindFor(wormholeId);
        if (group is null) return [];
        var tabs = new List<WormholeRecord>(group.Members.Count);
        foreach (var member in group.Members)
        {
            if (_records.TryGetValue(member, out var rec)) tabs.Add(rec);
        }
        return tabs;
    }

    /// <summary>Make <paramref name="dragged"/> a tab of the window hosting
    /// <paramref name="target"/>. The target's window stays exactly where it is and keeps its size;
    /// the dragged wormhole's window closes, and its record — geometry included — is left untouched
    /// so detaching later restores it at the size it had.</summary>
    public async Task MergeAsync(Guid dragged, Guid target, CancellationToken cancellationToken)
    {
        var group = _groups.Merge(dragged, target);
        if (group is null) return;
        await SaveGroupsAsync(cancellationToken).ConfigureAwait(true);

        if (!_live.TryGetValue(group.ParentId, out var host))
        {
            // Parent isn't on screen (hidden wormhole): nothing to fold into yet, the grouping is
            // recorded and takes effect when it next opens.
            return;
        }

        // Close whatever other windows the new members were living in, then point their ids at the
        // host. ForgetWindow (on Closed) clears the old registrations, so this order matters.
        foreach (var member in group.Members.ToList())
        {
            if (!_live.TryGetValue(member, out var other) || ReferenceEquals(other, host)) continue;
            try { other.CloseFromManager(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing {Id}'s window while merging failed", member); }
        }
        foreach (var member in group.Members) _live[member] = host;

        if (_records.TryGetValue(group.ActiveId, out var active)) host.SetActiveRecord(active);
        host.RefreshTabs();
        RepinWatcher(group.ParentId, host);
        _logger.LogInformation("Wormholes: {Dragged} merged into {Target} ({Count} tabs)",
            dragged, target, group.Members.Count);
    }

    /// <summary>Pull a tab out into a window of its own. It reappears at its own saved geometry:
    /// nothing overwrote it while it was a tab, because the hosting window only ever persisted the
    /// parent's.</summary>
    public async Task DetachAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        var group = _groups.FindFor(wormholeId);
        if (group is null) return;
        var wasParent = group.ParentId == wormholeId;
        var siblings = group.Members.Where(m => m != wormholeId).ToList();

        _groups.Detach(wormholeId);
        await SaveGroupsAsync(cancellationToken).ConfigureAwait(true);

        _live.TryGetValue(wormholeId, out var host);
        _live.Remove(wormholeId);

        if (host is not null && wasParent)
        {
            // The window was the parent's; the remaining tabs need one built on their new parent.
            try { host.CloseFromManager(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing the old parent window while detaching failed"); }
            var newParentId = _groups.FindFor(siblings.FirstOrDefault())?.ParentId ?? siblings.FirstOrDefault();
            if (newParentId is { } id && _records.TryGetValue(id, out var newParent)) SpawnWindow(newParent);
        }
        else if (host is not null && host.ActiveRecord.Id == wormholeId && siblings.Count > 0
                 && _records.TryGetValue(siblings[0], out var nextTab))
        {
            host.SetActiveRecord(nextTab);   // it was the tab on show
            RepinWatcher(nextTab.Id, host);
        }
        host?.RefreshTabs();

        if (_records.TryGetValue(wormholeId, out var detached)) SpawnWindow(detached);
        _logger.LogInformation("Wormholes: {Id} detached from its group", wormholeId);
    }

    /// <summary>Bring a tab to the front. Remembered, so a collapsed group reveals this one.</summary>
    public async Task SetActiveTabAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        var group = _groups.FindFor(wormholeId);
        if (group is null || group.ActiveId == wormholeId) return;
        _groups.SetActive(wormholeId);
        await SaveGroupsAsync(cancellationToken).ConfigureAwait(true);

        if (!_live.TryGetValue(wormholeId, out var host)) return;
        if (_records.TryGetValue(wormholeId, out var record))
        {
            host.SetActiveRecord(record);
            host.RefreshTabs();
            RepinWatcher(wormholeId, host);
        }
    }

    private async Task SaveGroupsAsync(CancellationToken cancellationToken)
    {
        try { await _store.SaveGroupsAsync(_groups, cancellationToken).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Saving wormhole tab groups failed"); }
    }

    /// <summary>Move the folder watcher onto whatever the window is now showing. Only one watcher
    /// per window: the tabs you can't see aren't listing anything.</summary>
    private void RepinWatcher(Guid key, WormholeWindow window)
    {
        // A watcher belongs to a window through the id it was filed under, and every id mapped to
        // this window is a candidate — the watcher may have been created under the parent's id or
        // under a tab's, depending on which path opened the window.
        foreach (var stale in _live.Where(kv => ReferenceEquals(kv.Value, window)).Select(kv => kv.Key).ToList())
        {
            if (!_watchers.Remove(stale, out var watcher)) continue;
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disposing a watcher while re-pinning failed"); }
        }
        if (_watchersPaused) return;
        var source = window.ActiveRecord.Portal?.SourcePath;
        if (!string.IsNullOrWhiteSpace(source)) _watchers[key] = CreateWatcher(source, window);
    }

    public async Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        if (_watchers.TryGetValue(wormholeId, out var watcher))
        {
            _watchers.Remove(wormholeId);
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "FolderWatcher dispose failed for {Id}", wormholeId); }
        }
        // Deleting a tab out of a group: the group loses a member (and dissolves if that leaves one
        // tab, which is just a wormhole again). Taken before the window handling below, because
        // whether the window survives depends on what's left of the group.
        var group = _groups.FindFor(wormholeId);
        var wasParent = group is not null && group.ParentId == wormholeId;
        if (group is not null)
        {
            _groups.Forget(wormholeId);
            try { await _store.SaveGroupsAsync(_groups, cancellationToken).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Saving tab groups after a delete failed"); }
        }

        if (_live.TryGetValue(wormholeId, out var window))
        {
            _live.Remove(wormholeId);
            _records.Remove(wormholeId);

            // Survivors still mapped to this window — the other tabs.
            var survivors = _live.Where(kv => ReferenceEquals(kv.Value, window)).Select(kv => kv.Key).ToList();

            // The window is built on the parent's record: geometry and the window-level flags are
            // read and written there. If the parent is the one going away the window has to be
            // rebuilt on whoever inherited the role, otherwise it would keep persisting itself
            // into a record that no longer exists.
            if (survivors.Count == 0 || wasParent)
            {
                try { window.CloseFromManager(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to close wormhole window {Id}", wormholeId); }

                if (survivors.Count > 0
                    && _groups.FindFor(survivors[0]) is { } reformed
                    && _records.TryGetValue(reformed.ParentId, out var newParent))
                {
                    SpawnWindow(newParent);
                }
                else if (survivors.Count == 1 && _records.TryGetValue(survivors[0], out var loneSurvivor))
                {
                    SpawnWindow(loneSurvivor);   // no group left: back to being its own wormhole
                }
            }
            else if (window.ActiveRecord.Id == wormholeId
                     && _records.TryGetValue(survivors[0], out var nextTab))
            {
                window.SetActiveRecord(nextTab);   // the deleted tab was the one on show
            }
        }
        _records.Remove(wormholeId);
        await _store.DeleteAsync(wormholeId, cancellationToken).ConfigureAwait(true);
        // Tell whoever is listening that this record no longer exists. An open Settings →
        // Wormholes panel uses it to drop the row; before this it only learned about deletions it
        // performed itself, so a delete from the wormhole's own chrome menu left a stale row
        // behind. Marshalled like RecordChanged: subscribers mutate UI-bound collections.
        if (Application.Current is { } app) _ = app.Dispatcher.BeginInvoke(() => RecordDeleted?.Invoke(this, wormholeId));
        else RecordDeleted?.Invoke(this, wormholeId);
    }

    public void CloseAll()
    {
        foreach (var (_, watcher) in _watchers)
        {
            try { watcher.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "FolderWatcher dispose failed during CloseAll"); }
        }
        _watchers.Clear();
        foreach (var window in LiveWindows)
        {
            try { window.CloseFromManager(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close wormhole window during CloseAll"); }
        }
        _live.Clear();
    }

    private void SpawnWindow(WormholeRecord record)
    {
        if (_live.ContainsKey(record.Id)) return;
        _records[record.Id] = record;

        // Tab group: one window for the whole group, owned by the parent. Whichever member the
        // caller happened to hand us, the window is built from the parent — that's where the
        // geometry and the window-level flags live — and every member is registered against it, so
        // a later lookup by any tab's id finds it.
        if (_groups.FindFor(record.Id) is { } group)
        {
            foreach (var member in group.Members)
            {
                if (!_live.TryGetValue(member, out var hosting)) continue;
                _live[record.Id] = hosting;   // the group's window already exists
                return;
            }

            if (record.Id != group.ParentId
                && _records.TryGetValue(group.ParentId, out var parent))
            {
                SpawnWindow(parent);          // build it from the parent instead
                if (_live.TryGetValue(group.ParentId, out var spawned)) _live[record.Id] = spawned;
                return;
            }
        }
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
            favicons: _favicons,
            // Colour picker behind the hamburger's "Accent colour…" entry.
            colors: _colors);
        window.DeleteRequested += async (_, id) =>
        {
            try { await DeleteAsync(id, CancellationToken.None).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Wormhole delete from menu failed for {Id}", id); }
        };
        // Drop the cache entry whenever the window goes away, no matter WHO closed it. Without
        // this, the chrome's hamburger → "Hide this wormhole" (which closes the window directly)
        // left a dead WormholeWindow in _live; the Settings checkbox then unchecked "Hidden",
        // ReconcileAsync found that stale entry and took the "refresh the existing window" branch
        // instead of spawning a new one, so nothing reappeared until the user toggled the box a
        // second time. Guarded by the identity check so the close that FOLLOWS a respawn (same id,
        // different window instance) can't evict the new window.
        window.Closed += (_, _) => ForgetWindow(record.Id, window);
        _live[record.Id] = window;

        // Hosting a group: point every member's id at this window and show the tab that was active
        // when the app last closed. Done before the watcher below, so the watcher follows the tab
        // actually on screen rather than the parent's folder.
        if (_groups.FindFor(record.Id) is { } hosted)
        {
            foreach (var member in hosted.Members) _live[member] = window;
            if (_records.TryGetValue(hosted.ActiveId, out var active)) window.SetActiveRecord(active);
            window.RefreshTabs();
        }

        // FolderWatcher pinned to the source of the tab currently on show. The watcher fires
        // Changed events on the dispatcher after a 300 ms quiet period; the window just
        // re-enumerates its source folder each tick. Disposed in DeleteAsync / CloseAll / the
        // window's Closed handler. A group needs only one: the tabs you can't see aren't listing
        // anything, and switching tabs re-pins it.
        var watched = window.ActiveRecord;
        if (!_watchersPaused && watched.Portal is { SourcePath: { Length: > 0 } sourcePath })
            _watchers[record.Id] = CreateWatcher(sourcePath, window);

        // WorkerW / Progman PARENTING (DesktopLayerHost.SetParent) stays disabled: it shifts the
        // window's coordinate space to the parent's client area, which doesn't match the
        // screen-coord Left/Top we persist, so every wormhole loaded off-screen by the delta
        // between the virtual origin and Progman's client origin.
        //
        // Desktop-layer semantics are delivered instead by DesktopOwnership (see that file for
        // the measurements): the wormhole becomes an OWNED window of Progman, which keeps it on
        // top of the desktop when "Show desktop" raises it, while leaving it a normal top-level
        // window in screen coordinates. Applied by the window itself at SourceInitialized.
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

    /// <summary>Build and start a folder watcher that refreshes <paramref name="window"/> on
    /// every debounced batch of file-system events.</summary>
    private FolderWatcher CreateWatcher(string sourcePath, WormholeWindow window)
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
        return watcher;
    }

    /// <summary>Remove a wormhole's live-window entry (and its folder watcher) from the caches,
    /// but only if <paramref name="window"/> is still the instance we have on record — a respawn
    /// can race a pending Closed callback from the previous instance.</summary>
    private void ForgetWindow(Guid id, WormholeWindow window)
    {
        if (!_live.TryGetValue(id, out var current) || !ReferenceEquals(current, window)) return;

        // A window hosting a group is registered under every tab's id. Closing it has to clear all
        // of them: leaving a sibling pointing at a dead window is exactly the stale-cache bug that
        // made "Hidden" need three clicks, one tab removed from being enough to repeat it.
        var owned = _live.Where(kv => ReferenceEquals(kv.Value, window)).Select(kv => kv.Key).ToList();
        foreach (var ownedId in owned)
        {
            _live.Remove(ownedId);
            if (!_watchers.Remove(ownedId, out var w)) continue;
            try { w.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "FolderWatcher dispose after window close failed for {Id}", ownedId); }
        }
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
    public event EventHandler<Guid>? RecordDeleted;

    public Task ReconcileAsync(WormholeRecord record, CancellationToken cancellationToken)
    {
        _records[record.Id] = record;

        // A tab that isn't the parent doesn't own the window, so reconciling it must not touch the
        // window's geometry or close it. Both of those were happening, and both were destructive:
        //
        //  - geometry: every member maps to the SAME window, so pushing each record's own X/Y/W/H
        //    left the group wearing whichever tab was reconciled last. Restoring a layout preset
        //    reconciles every record in turn, which is exactly how a group ended up the size of one
        //    of its tabs instead of its parent's.
        //  - hidden: the close path removed one id and closed the shared window, taking every other
        //    tab off the desktop with it and leaving their ids pointing at a dead window. The
        //    records were all still there, so Settings listed wormholes that had no window — which
        //    is what "they vanished from the desktop but I can see them in the list" was.
        //
        // Hidden / collapsed / topmost belong to the group as a whole, and the group's is the
        // parent's. A tab's own flag is still persisted and takes effect if it's ever detached.
        if (!_groups.GovernsWindow(record.Id))
        {
            if (_live.TryGetValue(record.Id, out var host))
            {
                if (host.ActiveRecord.Id == record.Id) host.RefreshFromRecord();
                host.RefreshTabs();   // a renamed or recoloured tab still has to redraw
            }
            return Task.CompletedTask;
        }

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
            // LogicalTop / LogicalHeight rather than Top / Height: a wormhole whose header is
            // revealed under the pointer is temporarily 32 px taller and higher than the record
            // says, and pushing raw values would fight that offset.
            if (Math.Abs(window.Left - record.Geometry.X) > 0.5) window.Left = record.Geometry.X;
            if (Math.Abs(window.LogicalTop - record.Geometry.Y) > 0.5) window.LogicalTop = record.Geometry.Y;
            if (Math.Abs(window.Width - record.Geometry.Width) > 0.5) window.Width = record.Geometry.Width;
            if (!record.IsRolled && Math.Abs(window.LogicalHeight - record.Geometry.Height) > 0.5)
                window.LogicalHeight = record.Geometry.Height;
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
        foreach (var window in LiveWindows)
        {
            var width = double.IsNaN(window.Width) ? 320 : window.Width;
            var height = double.IsNaN(window.Height) ? 240 : window.Height;
            var x = Math.Max(20, (screenW - width) / 2 - 100 + i * cascadeStep);
            var y = Math.Max(20, (screenH - height) / 2 - 100 + i * cascadeStep);
            window.Left = x;
            window.LogicalTop = y;
            window.Activate();
            i++;
        }
    }
}
