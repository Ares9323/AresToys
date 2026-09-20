using System.Linq;
using System.Text.Json.Serialization;

namespace AresToys.App.Services.Wormholes;

/// <summary>One tab group: several wormholes shown as tabs of a single window.
///
/// Deliberately holds nothing but ids. Everything a group needs to render already lives where it
/// lived before — the window's geometry is the parent's entry in <c>positions.json</c>, and each
/// tab keeps its own record in <c>wormholes.json</c> with its own colour, icon size and source
/// folder. That's what makes grouping non-destructive: a build that knows nothing about groups
/// ignores this file and opens the same wormholes it always did, each at its own size.</summary>
public sealed class WormholeGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Wormhole whose shape the group takes: position, size and the window-level flags
    /// (hidden / collapsed / topmost) are read from this one. Set to the wormhole that was dropped
    /// ON, so merging never moves the window the user aimed at.</summary>
    public Guid ParentId { get; set; }

    /// <summary>Tabs, in display order. The parent is not special in this list.</summary>
    public List<Guid> Members { get; set; } = new();

    /// <summary>Tab shown in the body: the last one clicked. Also what a collapsed group reveals
    /// when the pointer brings it up.</summary>
    public Guid ActiveId { get; set; }
}

/// <summary>Who is grouped with whom. Pure bookkeeping over ids — no windows, no files — so the
/// rules that are easy to get wrong (detaching the parent, a group that falls to a single tab,
/// a wormhole deleted while grouped) can be stated and tested on their own.
///
/// Invariant: a group always has at least two members. One tab is not a group, it's a wormhole,
/// so any operation that would leave a single member dissolves the group instead.</summary>
public sealed class WormholeGroups
{
    private readonly List<WormholeGroup> _groups = new();

    public IReadOnlyList<WormholeGroup> All => _groups;

    public WormholeGroups() { }

    public WormholeGroups(IEnumerable<WormholeGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _groups.AddRange(groups);
        foreach (var g in _groups) Normalise(g);
        _groups.RemoveAll(g => g.Members.Count < 2);
    }

    public WormholeGroup? FindFor(Guid wormholeId) =>
        _groups.FirstOrDefault(g => g.Members.Contains(wormholeId));

    public bool IsGrouped(Guid wormholeId) => FindFor(wormholeId) is not null;

    /// <summary>Whether this wormhole's record drives the window it appears in. True for anything
    /// ungrouped, and for a group's parent; false for the group's other tabs, which share a window
    /// they don't own.
    ///
    /// The distinction matters because window-level state — position, size, hidden, collapsed,
    /// topmost — belongs to the group as a whole and is read from the parent. Acting on a tab's own
    /// copy would move the window to whichever tab was handled last, or close a window that other
    /// tabs are still living in.</summary>
    public bool GovernsWindow(Guid wormholeId)
    {
        var group = FindFor(wormholeId);
        return group is null || group.ParentId == wormholeId;
    }

    /// <summary>Drop <paramref name="dragged"/> onto <paramref name="target"/>. If the target is
    /// already a tab of a group, the dragged wormhole joins that group and the group keeps its
    /// shape; otherwise a new group forms at the target's shape. Dragging a whole group brings all
    /// of its tabs across, and the emptied group disappears. Returns the resulting group, or null
    /// when there's nothing to do.</summary>
    public WormholeGroup? Merge(Guid dragged, Guid target)
    {
        if (dragged == target) return null;

        var targetGroup = FindFor(target);
        var draggedGroup = FindFor(dragged);
        if (targetGroup is not null && ReferenceEquals(targetGroup, draggedGroup))
        {
            // Already tabs of the same group: just bring the dropped one to the front.
            targetGroup.ActiveId = dragged;
            return targetGroup;
        }

        // Everything the dragged side brings: a whole group's tabs, or just itself.
        var incoming = draggedGroup?.Members.ToList() ?? [dragged];
        if (draggedGroup is not null) _groups.Remove(draggedGroup);

        var group = targetGroup;
        if (group is null)
        {
            group = new WormholeGroup { ParentId = target, Members = [target], ActiveId = target };
            _groups.Add(group);
        }

        foreach (var id in incoming)
        {
            if (!group.Members.Contains(id)) group.Members.Add(id);
        }
        group.ActiveId = dragged;
        Normalise(group);
        return group;
    }

    /// <summary>Pull a tab out of its group, back to being a wormhole of its own. The group hands
    /// the parent role on if it was the one leaving, and dissolves if a single tab is left.</summary>
    public void Detach(Guid wormholeId) => Remove(wormholeId);

    /// <summary>Drop a wormhole that no longer exists (deleted) out of its group. Same handling as
    /// <see cref="Detach"/> — the distinction is the caller's intent, not the bookkeeping.</summary>
    public void Forget(Guid wormholeId) => Remove(wormholeId);

    /// <summary>Remember which tab is on show. Ignored for a wormhole that isn't grouped, or one
    /// that isn't a member of the group it's being set on.</summary>
    public void SetActive(Guid wormholeId)
    {
        var group = FindFor(wormholeId);
        if (group is null) return;
        group.ActiveId = wormholeId;
    }

    /// <summary>Drop members that aren't in <paramref name="existing"/>, then any group left with
    /// fewer than two tabs. Run after loading, because groups.json is a separate file and can name
    /// wormholes that a rollback (or a hand edit) has removed from wormholes.json.</summary>
    public void PruneTo(IEnumerable<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var alive = existing as HashSet<Guid> ?? [.. existing];
        foreach (var group in _groups)
        {
            group.Members.RemoveAll(id => !alive.Contains(id));
            Normalise(group);
        }
        _groups.RemoveAll(g => g.Members.Count < 2);
    }

    private void Remove(Guid wormholeId)
    {
        var group = FindFor(wormholeId);
        if (group is null) return;
        group.Members.Remove(wormholeId);
        if (group.Members.Count < 2)
        {
            _groups.Remove(group);   // one tab is a wormhole, not a group
            return;
        }
        Normalise(group);
    }

    /// <summary>Make sure the group still points at members that exist in it: a parent or an
    /// active tab left dangling by a removal falls back to the first remaining member.</summary>
    private static void Normalise(WormholeGroup group)
    {
        if (group.Members.Count == 0) return;
        if (!group.Members.Contains(group.ParentId)) group.ParentId = group.Members[0];
        if (!group.Members.Contains(group.ActiveId)) group.ActiveId = group.Members[0];
    }
}

/// <summary>Persisted contents of <c>groups.json</c>. A file of its own, next to
/// <c>wormholes.json</c> and <c>positions.json</c> rather than inside them: grouping is then purely
/// additive, and a build without it — or a rollback — reads the other two exactly as before and
/// opens every tab as the separate wormhole it still is.</summary>
public sealed class WormholeGroupsFile
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public List<WormholeGroup> Groups { get; set; } = new();
}
