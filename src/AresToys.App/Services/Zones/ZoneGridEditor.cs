using System.Globalization;

namespace AresToys.App.Services.Zones;

/// <summary>Outcome of an editing operation. When <see cref="Changed"/> is false the layout is the unchanged
/// input and <see cref="Reason"/> says why. <see cref="Reason"/> can also hold a note on a successful edit
/// (for example an edge that was limited to a smaller move than requested).</summary>
public sealed record EditResult(GridLayout Layout, bool Changed, string? Reason);

/// <summary>Pure, immutable editing operations on a <see cref="GridLayout"/> for the zone editor: split, merge,
/// move a line, exact edges and snapping helpers. Positions, sizes and minimums are in percent units of
/// <see cref="GridLayout.PercentTotal"/>. Every returned layout passes <see cref="GridLayout.Validate"/>; an
/// edit that would produce an invalid one is reported through the reason instead of being applied.
/// Horizontal operations run on the transposed layout, so the column logic is written once.</summary>
public static class ZoneGridEditor
{
    /// <summary>Default smallest zone width or height: 3 percent of the work area.</summary>
    public const int DefaultMinZonePercent = 300;

    /// <summary>Value equality of two layouts: percents, cell map, spacing and sensitivity radius.</summary>
    public static bool SameLayout(GridLayout? a, GridLayout? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Spacing != b.Spacing || a.SensitivityRadius != b.SensitivityRadius) return false;
        if (!a.RowPercents.SequenceEqual(b.RowPercents) || !a.ColumnPercents.SequenceEqual(b.ColumnPercents)) return false;
        if (a.CellMap.Count != b.CellMap.Count) return false;
        for (int r = 0; r < a.CellMap.Count; r++)
        {
            if (!a.CellMap[r].SequenceEqual(b.CellMap[r])) return false;
        }

        return true;
    }

    /// <summary>Splits a zone in two at <paramref name="position"/> (absolute, along the axis, 0..10000). On
    /// a vertical split the right part is new, on a horizontal one the bottom part. The new part takes the
    /// next free zone index, so indexes stay contiguous and the other zones keep theirs, unless the zones were
    /// numbered in reading order (left to right, top to bottom) before the split: then they are renumbered in
    /// reading order again, so a layout built by splitting stays tidy. A position on a line
    /// that already crosses the zone splits along it; otherwise a line is added at that position (the grid
    /// column or row holding it is cut in two, and the other zones keep their rectangles). Refused when
    /// either side would be smaller than <paramref name="minZonePercent"/>.</summary>
    public static EditResult Split(GridLayout layout, int zoneIndex, ZoneAxis axis, int position,
        int minZonePercent = DefaultMinZonePercent)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        if (zoneIndex < 0 || zoneIndex >= layout.ZoneCount) return Unchanged(layout, "That zone does not exist.");
        int min = Math.Max(1, minZonePercent);
        var result = OnAxis(layout, axis, work => SplitVertical(work, zoneIndex, position, min));
        if (result.Changed && InReadingOrder(layout)) result = result with { Layout = InReadingOrderCopy(result.Layout) };
        return result;
    }

    /// <summary>True when the zone indexes already follow reading order, the order <see cref="Merge"/> produces.</summary>
    private static bool InReadingOrder(GridLayout layout)
    {
        var map = CopyMap(layout.CellMap);
        Renumber(map);
        return !map.Where((row, r) => !row.SequenceEqual(layout.CellMap[r])).Any();
    }

    private static GridLayout InReadingOrderCopy(GridLayout layout)
    {
        var map = CopyMap(layout.CellMap);
        Renumber(map);
        return layout with { CellMap = map };
    }

    /// <summary>Gives zone <paramref name="zone"/> the number <paramref name="newIndex"/>; the zone that had that
    /// number takes the old one of <paramref name="zone"/>, so the two swap and nothing else moves.</summary>
    public static EditResult SwapZones(GridLayout layout, int zone, int newIndex)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        int count = layout.ZoneCount;
        if (zone < 0 || zone >= count || newIndex < 0 || newIndex >= count) return Unchanged(layout, "That zone does not exist.");
        if (zone == newIndex) return new EditResult(layout, false, null);

        var map = CopyMap(layout.CellMap);
        foreach (var row in map)
        {
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c] == zone) row[c] = newIndex;
                else if (row[c] == newIndex) row[c] = zone;
            }
        }

        return new EditResult(layout with { CellMap = map }, true, null);
    }

    /// <summary>Renumbers every zone in reading order: left to right, then top to bottom.</summary>
    public static EditResult SortZones(GridLayout layout)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        var sorted = InReadingOrderCopy(layout);
        bool changed = sorted.CellMap.Where((row, r) => !row.SequenceEqual(layout.CellMap[r])).Any();
        return new EditResult(changed ? sorted : layout, changed, null);
    }

    /// <summary>Merges zones. The selection grows until no zone is cut by its bounding box (the PowerToys
    /// closure), everything inside becomes one zone, indexes are renumbered by first appearance in row-major
    /// order and lines that no longer separate anything are removed.</summary>
    public static EditResult Merge(GridLayout layout, IReadOnlyCollection<int> zoneIndexes)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        if (zoneIndexes is null) return Unchanged(layout, "Select at least two zones to merge.");

        var selected = zoneIndexes.Distinct().ToList();
        int count = layout.ZoneCount;
        foreach (int z in selected)
        {
            if (z < 0 || z >= count) return Unchanged(layout, "A selected zone does not exist.");
        }

        if (selected.Count < 2) return Unchanged(layout, "Select at least two zones to merge.");

        var boxes = CellBoxes(layout);
        int r0 = int.MaxValue, c0 = int.MaxValue, r1 = -1, c1 = -1;
        foreach (int z in selected) Extend(boxes[z]);

        // Grow the box until no zone is partly inside it. A zone is a rectangle, so its bounding box overlaps
        // the box exactly when one of its cells is inside.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var b in boxes)
            {
                bool overlaps = b.R0 <= r1 && b.R1 >= r0 && b.C0 <= c1 && b.C1 >= c0;
                bool inside = b.R0 >= r0 && b.R1 <= r1 && b.C0 >= c0 && b.C1 <= c1;
                if (overlaps && !inside)
                {
                    Extend(b);
                    grew = true;
                }
            }
        }

        int target = int.MaxValue;
        for (int z = 0; z < count; z++)
        {
            var b = boxes[z];
            if (b.R0 >= r0 && b.R1 <= r1 && b.C0 >= c0 && b.C1 <= c1) target = Math.Min(target, z);
        }

        var map = CopyMap(layout.CellMap);
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++) map[r][c] = target;
        }

        Renumber(map);
        var merged = layout with { CellMap = map };
        var compact = CompactCore(merged);
        return Finish(layout, compact, "Nothing to merge.");

        void Extend(CellBox b)
        {
            r0 = Math.Min(r0, b.R0);
            c0 = Math.Min(c0, b.C0);
            r1 = Math.Max(r1, b.R1);
            c1 = Math.Max(c1, b.C1);
        }
    }

    /// <summary>Moves a whole interior line. <paramref name="lineIndex"/> 0 is the line between column (or
    /// row) 0 and 1. The target is clamped to [previous line + min, next line - min] instead of being
    /// rejected; a line that is already closer than the minimum is never pushed further in. Changed is false
    /// only when the clamped position equals the current one.</summary>
    public static EditResult MoveLine(GridLayout layout, ZoneAxis axis, int lineIndex, int newPosition,
        int minZonePercent = DefaultMinZonePercent)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        int min = Math.Max(1, minZonePercent);
        return OnAxis(layout, axis, work => MoveVertical(work, lineIndex, newPosition, min));
    }

    /// <summary>Sets the edges of a zone to exact positions. Each given edge that is an interior line is moved
    /// with <see cref="MoveLine"/> (so it is clamped); an edge on the border of the work area (0 or 10000)
    /// cannot move and is ignored with a note. A moved line moves for every zone that touches it. Reason holds
    /// notes about ignored or limited edges, also when Changed is true.</summary>
    public static EditResult SetZoneEdges(GridLayout layout, int zoneIndex, int? left, int? top, int? right,
        int? bottom, int minZonePercent = DefaultMinZonePercent)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        if (zoneIndex < 0 || zoneIndex >= layout.ZoneCount) return Unchanged(layout, "That zone does not exist.");
        if (left is null && top is null && right is null && bottom is null)
            return Unchanged(layout, "No edge was given.");

        // Line indexes are positions in the grid, so they stay valid while lines move.
        TryCellBox(layout, zoneIndex, out var box);
        int cols = layout.ColumnPercents.Count;
        int rows = layout.RowPercents.Count;
        var notes = new List<string>();
        var current = layout;
        bool changed = false;

        Edge(left, "left", ZoneAxis.Vertical, box.C0 - 1, cols, b => b.Left);
        Edge(top, "top", ZoneAxis.Horizontal, box.R0 - 1, rows, b => b.Top);
        Edge(right, "right", ZoneAxis.Vertical, box.C1, cols, b => b.Right);
        Edge(bottom, "bottom", ZoneAxis.Horizontal, box.R1, rows, b => b.Bottom);

        if (!changed && notes.Count == 0) notes.Add("Nothing changed.");
        return new EditResult(current, changed, notes.Count == 0 ? null : string.Join(" ", notes));

        void Edge(int? value, string name, ZoneAxis axis, int lineIndex, int gridCount,
            Func<(int Left, int Top, int Right, int Bottom), int> select)
        {
            if (value is not int wanted) return;
            if (lineIndex < 0 || lineIndex >= gridCount - 1)
            {
                notes.Add($"The {name} edge is on the border of the work area and cannot move.");
                return;
            }

            int before = select(ZoneBounds(current, zoneIndex));
            if (wanted == before) return;

            var moved = MoveLine(current, axis, lineIndex, wanted, minZonePercent);
            if (!moved.Changed)
            {
                notes.Add($"The {name} edge cannot move further.");
                return;
            }

            current = moved.Layout;
            changed = true;
            int actual = select(ZoneBounds(current, zoneIndex));
            if (actual != wanted) notes.Add($"The {name} edge was limited to {FormatPercent(actual)}.");
        }
    }

    /// <summary>Removes every row or column line that no longer separates two different zones anywhere,
    /// adding up the percents, so the model stays minimal.</summary>
    public static EditResult Compact(GridLayout layout)
    {
        if (CheckInput(layout) is { } error) return Unchanged(layout, error);
        return Finish(layout, CompactCore(layout), "The grid is already minimal.");
    }

    /// <summary>Edges of a zone in percent units (Left, Top, Right, Bottom), for number fields and hit areas.
    /// Returns (0, 0, 0, 0) when the zone does not exist.</summary>
    public static (int Left, int Top, int Right, int Bottom) ZoneBounds(GridLayout layout, int zoneIndex)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!TryCellBox(layout, zoneIndex, out var box)) return (0, 0, 0, 0);
        var cols = Prefix(layout.ColumnPercents);
        var rows = Prefix(layout.RowPercents);
        return (cols[box.C0], rows[box.R0], cols[box.C1 + 1], rows[box.R1 + 1]);
    }

    /// <summary>Positions of the interior lines (the borders 0 and 10000 are left out), in increasing order.
    /// Vertical gives the lines between columns, Horizontal the ones between rows. Use as snap candidates.</summary>
    public static IReadOnlyList<int> InteriorLines(GridLayout layout, ZoneAxis axis)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var percents = axis == ZoneAxis.Vertical ? layout.ColumnPercents : layout.RowPercents;
        var lines = new List<int>();
        int acc = 0;
        for (int i = 0; i < percents.Count - 1; i++)
        {
            acc += percents[i];
            lines.Add(acc);
        }

        return lines;
    }

    // ---- vertical (column) logic; horizontal edits run on the transposed layout ----

    private static EditResult SplitVertical(GridLayout work, int zoneIndex, int position, int min)
    {
        var cols = work.ColumnPercents;
        var cum = Prefix(cols);
        TryCellBox(work, zoneIndex, out var box);
        int left = cum[box.C0];
        int right = cum[box.C1 + 1];
        if (position - left < min || right - position < min)
            return Unchanged(work, $"Too small: each side of the split must be at least {FormatPercent(min)} of the work area.");

        // Either the position is on a line already inside the zone, or it cuts one column in two.
        int cut = -1;
        for (int k = box.C0; k < box.C1; k++)
        {
            if (cum[k + 1] == position) cut = k;
        }

        bool existingLine = cut >= 0;
        if (!existingLine)
        {
            for (int c = box.C0; c <= box.C1; c++)
            {
                if (cum[c] < position && position < cum[c + 1]) cut = c;
            }
        }

        if (cut < 0) return Unchanged(work, "That position is not inside the zone.");

        var newCols = new List<int>();
        for (int i = 0; i < cols.Count; i++)
        {
            if (i == cut && !existingLine)
            {
                newCols.Add(position - cum[i]);
                newCols.Add(cum[i + 1] - position);
            }
            else
            {
                newCols.Add(cols[i]);
            }
        }

        var map = new int[work.CellMap.Count][];
        for (int r = 0; r < map.Length; r++)
        {
            var row = new List<int>(newCols.Count);
            for (int i = 0; i < cols.Count; i++)
            {
                row.Add(work.CellMap[r][i]);
                if (i == cut && !existingLine) row.Add(work.CellMap[r][i]);
            }

            map[r] = row.ToArray();
        }

        // The new part starts at the column after the cut and ends where the zone ended.
        int firstNew = cut + 1;
        int lastNew = existingLine ? box.C1 : box.C1 + 1;
        int newIndex = work.ZoneCount;
        for (int r = box.R0; r <= box.R1; r++)
        {
            for (int c = firstNew; c <= lastNew; c++) map[r][c] = newIndex;
        }

        var next = work with { ColumnPercents = newCols.ToArray(), CellMap = map };
        return Finish(work, next, "Nothing changed.");
    }

    private static EditResult MoveVertical(GridLayout work, int lineIndex, int newPosition, int min)
    {
        var cols = work.ColumnPercents;
        if (lineIndex < 0 || lineIndex >= cols.Count - 1) return Unchanged(work, "That line does not exist.");

        var cum = Prefix(cols);
        int current = cum[lineIndex + 1];
        int low = Math.Min(cum[lineIndex] + min, current);
        int high = Math.Max(cum[lineIndex + 2] - min, current);
        int target = Math.Clamp(newPosition, low, high);
        if (target == current)
        {
            return Unchanged(work, newPosition == current
                ? "The line is already there."
                : "The line cannot move further: the neighbouring zones are at their minimum size.");
        }

        var newCols = cols.ToArray();
        newCols[lineIndex] += target - current;
        newCols[lineIndex + 1] -= target - current;
        return Finish(work, work with { ColumnPercents = newCols }, "Nothing changed.");
    }

    // ---- helpers ----

    private static EditResult OnAxis(GridLayout layout, ZoneAxis axis, Func<GridLayout, EditResult> vertical)
    {
        if (axis == ZoneAxis.Vertical) return vertical(layout);
        var result = vertical(Transpose(layout));
        return result.Changed
            ? result with { Layout = Transpose(result.Layout) }
            : new EditResult(layout, false, result.Reason);
    }

    private static GridLayout Transpose(GridLayout layout)
    {
        int rows = layout.RowPercents.Count;
        int cols = layout.ColumnPercents.Count;
        var map = new int[cols][];
        for (int c = 0; c < cols; c++)
        {
            map[c] = new int[rows];
            for (int r = 0; r < rows; r++) map[c][r] = layout.CellMap[r][c];
        }

        return layout with { RowPercents = layout.ColumnPercents, ColumnPercents = layout.RowPercents, CellMap = map };
    }

    private static string? CheckInput(GridLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return GridLayout.Validate(layout);
    }

    private static EditResult Unchanged(GridLayout layout, string reason) => new(layout, false, reason);

    // Last gate: the result must be a valid layout and different from the input.
    private static EditResult Finish(GridLayout original, GridLayout next, string sameReason)
    {
        if (GridLayout.Validate(next) is { } error) return Unchanged(original, error);
        return SameLayout(original, next) ? Unchanged(original, sameReason) : new EditResult(next, true, null);
    }

    private static int[] Prefix(IReadOnlyList<int> percents)
    {
        var sums = new int[percents.Count + 1];
        for (int i = 0; i < percents.Count; i++) sums[i + 1] = sums[i] + percents[i];
        return sums;
    }

    private static int[][] CopyMap(IReadOnlyList<IReadOnlyList<int>> map)
    {
        var copy = new int[map.Count][];
        for (int r = 0; r < copy.Length; r++) copy[r] = map[r].ToArray();
        return copy;
    }

    // Renumbers zone indexes by first appearance in row-major order, so they are contiguous from 0.
    private static void Renumber(int[][] map)
    {
        var ids = new Dictionary<int, int>();
        foreach (var row in map)
        {
            for (int c = 0; c < row.Length; c++)
            {
                if (!ids.TryGetValue(row[c], out int id))
                {
                    id = ids.Count;
                    ids[row[c]] = id;
                }

                row[c] = id;
            }
        }
    }

    private static GridLayout CompactCore(GridLayout layout)
    {
        var map = layout.CellMap;
        int rows = layout.RowPercents.Count;
        int cols = layout.ColumnPercents.Count;

        // A line between two columns (rows) is needed when some row (column) has different zones on its sides.
        // Dropping one line never changes whether another is needed, so all are tested on the original map.
        var keepCol = new bool[cols];
        keepCol[cols - 1] = true;
        for (int c = 0; c < cols - 1; c++)
        {
            for (int r = 0; r < rows; r++)
            {
                if (map[r][c] != map[r][c + 1])
                {
                    keepCol[c] = true;
                    break;
                }
            }
        }

        var keepRow = new bool[rows];
        keepRow[rows - 1] = true;
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (map[r][c] != map[r + 1][c])
                {
                    keepRow[r] = true;
                    break;
                }
            }
        }

        var newCols = new List<int>();
        var colSource = new List<int>();
        int acc = 0;
        for (int c = 0; c < cols; c++)
        {
            acc += layout.ColumnPercents[c];
            if (!keepCol[c]) continue;
            newCols.Add(acc);
            colSource.Add(c);
            acc = 0;
        }

        var newRows = new List<int>();
        var rowSource = new List<int>();
        acc = 0;
        for (int r = 0; r < rows; r++)
        {
            acc += layout.RowPercents[r];
            if (!keepRow[r]) continue;
            newRows.Add(acc);
            rowSource.Add(r);
            acc = 0;
        }

        var newMap = new int[rowSource.Count][];
        for (int r = 0; r < newMap.Length; r++)
        {
            newMap[r] = new int[colSource.Count];
            for (int c = 0; c < colSource.Count; c++) newMap[r][c] = map[rowSource[r]][colSource[c]];
        }

        return layout with { RowPercents = newRows.ToArray(), ColumnPercents = newCols.ToArray(), CellMap = newMap };
    }

    private readonly record struct CellBox(int R0, int R1, int C0, int C1);

    // Bounding box in grid cells of every zone, indexed by zone index. Input must be valid.
    private static CellBox[] CellBoxes(GridLayout layout)
    {
        int count = layout.ZoneCount;
        var boxes = new CellBox[count];
        for (int z = 0; z < count; z++) boxes[z] = new CellBox(int.MaxValue, -1, int.MaxValue, -1);
        for (int r = 0; r < layout.CellMap.Count; r++)
        {
            for (int c = 0; c < layout.CellMap[r].Count; c++)
            {
                int z = layout.CellMap[r][c];
                var b = boxes[z];
                boxes[z] = new CellBox(Math.Min(b.R0, r), Math.Max(b.R1, r), Math.Min(b.C0, c), Math.Max(b.C1, c));
            }
        }

        return boxes;
    }

    private static bool TryCellBox(GridLayout layout, int zoneIndex, out CellBox box)
    {
        box = default;
        if (zoneIndex < 0) return false;
        int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
        for (int r = 0; r < layout.CellMap.Count; r++)
        {
            for (int c = 0; c < layout.CellMap[r].Count; c++)
            {
                if (layout.CellMap[r][c] != zoneIndex) continue;
                r0 = Math.Min(r0, r);
                r1 = Math.Max(r1, r);
                c0 = Math.Min(c0, c);
                c1 = Math.Max(c1, c);
            }
        }

        if (r1 < 0) return false;
        box = new CellBox(r0, r1, c0, c1);
        return true;
    }

    private static string FormatPercent(int percentUnits) =>
        (percentUnits / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
