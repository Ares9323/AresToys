namespace AresToys.App.Services.Zones;

/// <summary>Outcome of a canvas edit: the resulting layout, whether it differs from the input, and the reason for a
/// refusal (null when accepted or when nothing changed).</summary>
public sealed record CanvasEditResult(CanvasLayout Layout, bool Changed, string? Reason);

/// <summary>Editing operations on a canvas layout (free rectangles that may overlap). Every call returns a new
/// layout and never changes its input. Coordinates are in 1/10000 of the work area. Bad indexes and sizes that are
/// too small come back as refusals with a Reason, they never throw.</summary>
public static class ZoneCanvasEditor
{
    private const int Full = GridLayout.PercentTotal;
    private const string NoSuchZone = "No such zone.";
    private const string TooSmall = "That zone is too small.";

    /// <summary>One free rectangle per zone of the grid, in zone order. The grid spacing is dropped; the sensitivity
    /// radius is kept.</summary>
    public static CanvasLayout FromGrid(GridLayout grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var zones = new List<CanvasZone>(grid.ZoneCount);
        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var (left, top, right, bottom) = ZoneGridEditor.ZoneBounds(grid, i);
            zones.Add(new CanvasZone(left, top, right, bottom));
        }

        return new CanvasLayout(zones, grid.SensitivityRadius);
    }

    /// <summary>Value equality of two canvas layouts: the radius and every zone, in order.</summary>
    public static bool SameLayout(CanvasLayout? a, CanvasLayout? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.SensitivityRadius != b.SensitivityRadius || a.ZoneCount != b.ZoneCount) return false;
        for (int i = 0; i < a.ZoneCount; i++)
        {
            if (!a.Zones[i].Equals(b.Zones[i])) return false;
        }

        return true;
    }

    /// <summary>Adds a zone at the end. The corners may come in any order and are clamped to the work area.</summary>
    public static CanvasEditResult AddZone(CanvasLayout layout, int left, int top, int right, int bottom)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var zone = Normalized(left, top, right, bottom);
        if (IsTooSmall(zone)) return Refuse(layout, TooSmall);
        if (layout.ZoneCount >= CanvasLayout.MaxZones)
            return Refuse(layout, $"A layout can have at most {CanvasLayout.MaxZones} zones.");

        var zones = new List<CanvasZone>(layout.Zones) { zone };
        return new CanvasEditResult(new CanvasLayout(zones, layout.SensitivityRadius), true, null);
    }

    /// <summary>Moves a zone to position <paramref name="newIndex"/> by swapping it with the zone that is there.</summary>
    public static CanvasEditResult SwapZones(CanvasLayout layout, int zone, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsIndex(layout, zone) || !IsIndex(layout, newIndex)) return Refuse(layout, NoSuchZone);
        if (zone == newIndex) return Unchanged(layout);

        var zones = layout.Zones.ToArray();
        (zones[zone], zones[newIndex]) = (zones[newIndex], zones[zone]);
        return new CanvasEditResult(layout with { Zones = zones }, true, null);
    }

    /// <summary>Orders the zones top to bottom, and left to right among zones that start at the same height.</summary>
    public static CanvasEditResult SortZones(CanvasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var zones = layout.Zones.OrderBy(z => z.Top).ThenBy(z => z.Left).ToArray();
        if (zones.SequenceEqual(layout.Zones)) return Unchanged(layout);
        return new CanvasEditResult(layout with { Zones = zones }, true, null);
    }

    /// <summary>Replaces only the given edges of a zone. Edges are clamped to the work area; the zone keeps its
    /// index.</summary>
    public static CanvasEditResult SetZoneEdges(CanvasLayout layout, int zone, int? left, int? top, int? right, int? bottom)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsIndex(layout, zone)) return Refuse(layout, NoSuchZone);

        var old = layout.Zones[zone];
        var next = new CanvasZone(
            Clamp(left ?? old.Left),
            Clamp(top ?? old.Top),
            Clamp(right ?? old.Right),
            Clamp(bottom ?? old.Bottom));
        if (IsTooSmall(next)) return Refuse(layout, TooSmall);
        if (next.Equals(old)) return Unchanged(layout);

        return new CanvasEditResult(WithZone(layout, zone, next), true, null);
    }

    /// <summary>Moves a zone by (dx, dy) keeping its size. The move is clamped so the zone stays inside the work
    /// area; the zone is never shrunk.</summary>
    public static CanvasEditResult MoveZone(CanvasLayout layout, int zone, int dx, int dy)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsIndex(layout, zone)) return Refuse(layout, NoSuchZone);

        var old = layout.Zones[zone];
        int width = old.Right - old.Left;
        int height = old.Bottom - old.Top;
        int left = Math.Clamp(old.Left + dx, 0, Math.Max(0, Full - width));
        int top = Math.Clamp(old.Top + dy, 0, Math.Max(0, Full - height));
        var moved = new CanvasZone(left, top, left + width, top + height);
        if (moved.Equals(old)) return Unchanged(layout);

        return new CanvasEditResult(WithZone(layout, zone, moved), true, null);
    }

    /// <summary>Removes a zone. The zones after it shift down by one, so their numbers change. A layout keeps at
    /// least one zone.</summary>
    public static CanvasEditResult RemoveZone(CanvasLayout layout, int zone)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsIndex(layout, zone)) return Refuse(layout, NoSuchZone);
        if (layout.ZoneCount == 1) return Refuse(layout, "A layout needs at least one zone.");

        var zones = new List<CanvasZone>(layout.Zones);
        zones.RemoveAt(zone);
        return new CanvasEditResult(new CanvasLayout(zones, layout.SensitivityRadius), true, null);
    }

    private static bool IsIndex(CanvasLayout layout, int zone) => zone >= 0 && zone < layout.ZoneCount;

    private static int Clamp(int value) => Math.Clamp(value, 0, Full);

    private static CanvasZone Normalized(int left, int top, int right, int bottom) => new(
        Clamp(Math.Min(left, right)),
        Clamp(Math.Min(top, bottom)),
        Clamp(Math.Max(left, right)),
        Clamp(Math.Max(top, bottom)));

    private static bool IsTooSmall(CanvasZone zone) =>
        zone.Right - zone.Left < CanvasLayout.MinSize || zone.Bottom - zone.Top < CanvasLayout.MinSize;

    private static CanvasLayout WithZone(CanvasLayout layout, int index, CanvasZone zone)
    {
        var zones = new List<CanvasZone>(layout.Zones);
        zones[index] = zone;
        return new CanvasLayout(zones, layout.SensitivityRadius);
    }

    private static CanvasEditResult Refuse(CanvasLayout layout, string reason) => new(layout, false, reason);

    private static CanvasEditResult Unchanged(CanvasLayout layout) => new(layout, false, null);
}
