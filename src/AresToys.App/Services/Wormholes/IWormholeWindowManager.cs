namespace AresToys.App.Services.Wormholes;

/// <summary>Lifecycle of the WPF wormhole windows. Owns the mapping <c>WormholeRecord.Id →
/// live WormholeWindow</c>: hydrates one window per persisted record on startup, spawns a new
/// one when the user creates a wormhole from Settings / tray, and disposes them all on app
/// shutdown.
///
/// Hibernation / WorkerW parenting (per the spec §4.4) land in later milestones. The skeleton
/// just opens always-on-top transparent windows; the seam to swap in desktop-layer parenting
/// is a single call inside <c>SpawnWindowAsync</c> wrapped by an "is desktop layer enabled"
/// flag.</summary>
public interface IWormholeWindowManager
{
    /// <summary>Hydrates the records from <see cref="IWormholeStore"/> and spawns a window for
    /// every non-hidden one. Idempotent — safe to call multiple times but expected once at
    /// startup. If the wormholes module is disabled (<see cref="ModuleSettings.WormholesEnabled"/>
    /// false), the caller skips this entirely; the manager itself doesn't double-check the
    /// flag.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Creates a new wormhole record + window mirroring <paramref name="sourceFolder"/>.
    /// Caller validates the folder exists.</summary>
    Task<WormholeRecord> CreateAsync(string title, string sourceFolder, CancellationToken cancellationToken);

    /// <summary>Removes the wormhole record (its on-disk folder content is owned by the user
    /// and stays put) and closes the open window if any.</summary>
    Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken);

    /// <summary>Closes every live wormhole window without removing records — used by the module
    /// teardown path (Settings → toggle Wormholes OFF) and by app shutdown.</summary>
    void CloseAll();

    /// <summary>Repositions every currently-open wormhole onto the primary monitor in a cascade
    /// and activates them. User-triggered recovery path (tray menu) for when wormholes end up
    /// off-screen — monitor disconnect, weird DPI scaling, multi-monitor layout change between
    /// sessions.</summary>
    void RecenterAll();

    /// <summary>Reconcile the live window for <paramref name="record"/> with the record's
    /// current state. Used by the Wormholes Settings panel after mutating Lock / Hidden / Title
    /// / Geometry: if IsHidden flipped true the live window is closed (record stays); if
    /// IsHidden flipped false a new window is spawned; otherwise the live window's visuals are
    /// refreshed in place AND its Left/Top/Width/Height are pushed from the record so the panel
    /// can drive position from its TextBox inputs. No-op when the wormhole module is disabled
    /// (no live windows exist).</summary>
    Task ReconcileAsync(WormholeRecord record, CancellationToken cancellationToken);

    /// <summary>Raised when a wormhole record is persisted to disk — covers user drag/resize of
    /// the chrome (every LocationChanged / SizeChanged on the live window flows through this),
    /// lock / hidden / title mutations from the Settings panel, and the chrome's hamburger
    /// actions. The Wormholes Settings tab subscribes to keep its grid display (especially the
    /// X / Y / W / H cells) in sync with what the user does on the live wormhole. Fires on the
    /// UI dispatcher thread.</summary>
    event EventHandler<Guid>? RecordChanged;

    /// <summary>Flip <see cref="WormholeRecord.IsHidden"/> on every persisted record. Hidden
    /// wormholes have their live window closed (record stays); un-hidden wormholes get a fresh
    /// window spawned. Used by the workflow "Hide all / Show all" tasks and by the future
    /// global hotkey of the same name.</summary>
    Task SetAllHiddenAsync(bool hidden, CancellationToken cancellationToken);

    /// <summary>Flip <see cref="WormholeRecord.IsLocked"/> on every record. Locked wormholes
    /// can't be dragged or resized; the lock glyph in the chrome reflects the new state.</summary>
    Task SetAllLockedAsync(bool locked, CancellationToken cancellationToken);

    /// <summary>Flip <see cref="WormholeRecord.IsRolled"/> on every record — collapses each
    /// wormhole to header-only height (or restores to UnrolledHeight). Useful for reclaiming
    /// desktop space without hiding the wormholes outright.</summary>
    Task SetAllRolledAsync(bool rolled, CancellationToken cancellationToken);

    /// <summary>Smart-toggle: if ANY wormhole is currently visible (IsHidden=false), hide all;
    /// otherwise (everything already hidden) show all. The "any" semantics matches user mental
    /// model — "make them go away" is the dominant gesture when at least one is in the way.</summary>
    Task ToggleAllHiddenAsync(CancellationToken cancellationToken);

    /// <summary>Smart-toggle: if ANY wormhole is unlocked, lock all; otherwise unlock all.</summary>
    Task ToggleAllLockedAsync(CancellationToken cancellationToken);

    /// <summary>Smart-toggle: if ANY wormhole is uncollapsed, collapse all; otherwise uncollapse
    /// all.</summary>
    Task ToggleAllRolledAsync(CancellationToken cancellationToken);

    /// <summary>Called by a wormhole window when the user clicks/selects an item inside it
    /// (without Ctrl/Shift) — clears the item selection on every OTHER live wormhole, so the
    /// "selected" highlight is always single-wormhole. Mirrors Explorer's per-folder selection
    /// model rather than the default WPF per-ListBox behaviour (which leaves stale selections
    /// in sibling wormholes). Pass the calling window so we don't recursively clear it.</summary>
    void NotifyItemSelectionTaken(System.Windows.Window source);

    /// <summary>Called by a wormhole window when ANY mouse-down on its chrome (or item area)
    /// happens — used to drive the "focused wormhole" highlight in the Settings panel. The
    /// argument is the underlying record id; subscribers can ignore the value if they only
    /// care about the fact a focus event fired. Fires on the UI dispatcher thread.</summary>
    event EventHandler<Guid>? WormholeFocused;

    /// <summary>Notify the manager that the user just interacted with this wormhole — fires
    /// <see cref="WormholeFocused"/> with the matching record id so the Settings panel can
    /// highlight the corresponding row. Idempotent: refiring with the same source is cheap.</summary>
    void NotifyWormholeFocused(System.Windows.Window source);
}
