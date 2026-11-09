namespace AresToys.App.Services.Wormholes;

/// <summary>What a live <see cref="Views.WormholeWindow"/> asks of its manager. It adds to
/// <see cref="IWormholeWindowManager"/> the callbacks that take WPF types, which only make sense in the process that
/// owns the windows (the non elevated satellite) and can never cross the channel. The main app talks to the
/// wormholes through <see cref="IWormholeWindowManager"/> alone (a proxy over the satellite channel, see
/// <see cref="WormholeWire"/>); <see cref="WormholeWindowManager"/> implements both interfaces and passes itself to
/// every window as this one.</summary>
public interface IWormholeWindowHost : IWormholeWindowManager
{
    /// <summary>Which wormhole's header is under a screen point (physical pixels), ignoring the
    /// window being dragged. Used at the end of a drag to decide whether the user dropped one
    /// wormhole onto another's header, which is the gesture that merges them.</summary>
    Guid? FindHeaderTargetAt(int screenX, int screenY, Views.WormholeWindow exclude);

    /// <summary>Called by a wormhole window when the user clicks/selects an item inside it
    /// (without Ctrl/Shift) — clears the item selection on every OTHER live wormhole, so the
    /// "selected" highlight is always single-wormhole. Mirrors Explorer's per-folder selection
    /// model rather than the default WPF per-ListBox behaviour (which leaves stale selections
    /// in sibling wormholes). Pass the calling window so we don't recursively clear it.</summary>
    void NotifyItemSelectionTaken(System.Windows.Window source);

    /// <summary>Notify the manager that the user just interacted with this wormhole — fires
    /// <see cref="IWormholeWindowManager.WormholeFocused"/> with the matching record id so the Settings panel can
    /// highlight the corresponding row. Idempotent: refiring with the same source is cheap.</summary>
    void NotifyWormholeFocused(System.Windows.Window source);
}
