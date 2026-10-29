namespace AresToys.App.Services.Zones;

/// <summary>Maps a cursor position to zone indexes. Follows PowerToys Layout::ZonesFromPoint: every zone within the
/// sensitivity radius of the cursor is captured. Captured zones that do not overlap are all returned, so a cursor
/// near the line between two zones snaps to both, as if Ctrl were held. When some captured zones overlap, exactly
/// one is chosen by the overlap algorithm.</summary>
public static class ZoneHitTester
{
    /// <summary>The zone indexes under the point, in ascending order: every captured zone when they do not overlap,
    /// otherwise the single one <paramref name="algorithm"/> picks.</summary>
    public static IReadOnlyList<int> ZonesFromPoint(IReadOnlyList<PixelRect> zones, int x, int y, int sensitivityRadius,
        ZoneOverlapAlgorithm algorithm = ZoneOverlapAlgorithm.ClosestCenter)
    {
        var strict = new List<int>();
        var captured = new List<int>();
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z.IsEmpty) continue;
            if (z.Contains(x, y)) strict.Add(i);
            if (x >= z.Left - sensitivityRadius && x <= z.Right + sensitivityRadius &&
                y >= z.Top - sensitivityRadius && y <= z.Bottom + sensitivityRadius)
            {
                captured.Add(i);
            }
        }

        // A lone zone reached only through the margin does not count (PowerToys Layout.cpp 199-211).
        if (captured.Count == 1 && strict.Count == 0) return [];

        // Captured zones that do not overlap are all returned (PowerToys Layout.cpp 213-270).
        if (!AnyOverlap(zones, captured, sensitivityRadius)) return captured;

        return [Choose(zones, captured, x, y, sensitivityRadius, algorithm)];
    }

    // Two zones overlap when their intersection is deeper than the sensitivity radius in both directions.
    private static bool AnyOverlap(IReadOnlyList<PixelRect> zones, List<int> indexes, int radius)
    {
        for (int i = 0; i < indexes.Count; i++)
        {
            for (int j = i + 1; j < indexes.Count; j++)
            {
                var a = zones[indexes[i]];
                var b = zones[indexes[j]];
                if ((long)Math.Max(a.Top, b.Top) + radius < Math.Min(a.Bottom, b.Bottom) &&
                    (long)Math.Max(a.Left, b.Left) + radius < Math.Min(a.Right, b.Right))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The zones highlighted when several are merged (Ctrl held): the bounding box of the given
    /// zones is grown until every zone lying fully inside it is included, then all of them are returned in
    /// ascending order. Follows PowerToys Layout::GetCombinedZoneRange.</summary>
    public static IReadOnlyList<int> CombinedRange(IReadOnlyList<PixelRect> zones, IReadOnlyList<int> anchorAndCurrent)
    {
        var set = new SortedSet<int>();
        foreach (var i in anchorAndCurrent)
        {
            if (i >= 0 && i < zones.Count && !zones[i].IsEmpty) set.Add(i);
        }

        if (set.Count == 0) return [];

        bool grown;
        do
        {
            grown = false;
            var box = ZoneGeometry.Union(zones, set);
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z.IsEmpty || set.Contains(i)) continue;
                if (z.Left >= box.Left && z.Top >= box.Top && z.Right <= box.Right && z.Bottom <= box.Bottom)
                {
                    set.Add(i);
                    grown = true;
                }
            }
        }
        while (grown);

        return [.. set];
    }

    private static int Choose(IReadOnlyList<PixelRect> zones, List<int> candidates, int x, int y, int radius,
        ZoneOverlapAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case ZoneOverlapAlgorithm.Smallest:
                return Best(candidates, (a, b) => zones[a].Area < zones[b].Area);
            case ZoneOverlapAlgorithm.Largest:
                return Best(candidates, (a, b) => zones[a].Area > zones[b].Area);
            case ZoneOverlapAlgorithm.Positional:
                return Positional(zones, candidates, x, y, radius);
            default:
                return Best(candidates, (a, b) => CloserToCenter(zones[a], zones[b], x, y));
        }
    }

    // ZoneSelectPriority in PowerToys Layout.cpp: the first candidate that beats the current choice wins.
    private static int Best(List<int> candidates, Func<int, int, bool> better)
    {
        var chosen = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (better(candidates[i], chosen)) chosen = candidates[i];
        }

        return chosen;
    }

    // ZoneSelectClosestCenter: nearer center wins, unless the two centers almost coincide, then the smaller zone.
    private static bool CloserToCenter(PixelRect first, PixelRect second, int x, int y)
    {
        const long OverlappingCentersSensitivity = 75;
        var (c1x, c1y) = Center(first);
        var (c2x, c2y) = Center(second);
        if (Squared(c1x - c2x, c1y - c2y) > OverlappingCentersSensitivity)
        {
            return Squared(c1x - x, c1y - y) < Squared(c2x - x, c2y - y);
        }

        return first.Area < second.Area;
    }

    private static (long X, long Y) Center(PixelRect z) => ((z.Left + (long)z.Right) / 2, (z.Top + (long)z.Bottom) / 2);

    private static long Squared(long dx, long dy) => dx * dx + dy * dy;

    // ZoneSelectSubregion: the region shared by all candidates is cut into equal strips along its longer side.
    private static int Positional(IReadOnlyList<PixelRect> zones, List<int> candidates, int x, int y, int radius)
    {
        var half = radius / 2;
        long top = long.MinValue, left = long.MinValue, bottom = long.MaxValue, right = long.MaxValue;
        foreach (var index in candidates)
        {
            var z = zones[index];
            top = Math.Max(top, z.Top - half);
            left = Math.Max(left, z.Left - half);
            bottom = Math.Min(bottom, z.Bottom + half);
            right = Math.Min(right, z.Right + half);
        }

        var width = Math.Max(right - left, 1);
        var height = Math.Max(bottom - top, 1);
        var position = height > width
            ? (y - top) * candidates.Count / height
            : (x - left) * candidates.Count / width;
        return candidates[(int)Math.Clamp(position, 0, candidates.Count - 1)];
    }
}
