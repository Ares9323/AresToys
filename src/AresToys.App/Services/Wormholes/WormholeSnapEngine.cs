namespace AresToys.App.Services.Wormholes;

/// <summary>Which edges of the wormhole the user is currently dragging. <see cref="Move"/> means
/// the whole window travels (size fixed); any combination of the four edge flags means a resize
/// and only those edges may be adjusted by the snap.</summary>
[Flags]
public enum SnapEdges
{
    None   = 0,
    Left   = 1,
    Top    = 2,
    Right  = 4,
    Bottom = 8,
    Move   = 16,
}

/// <summary>Screen rectangle in PHYSICAL pixels — the coordinate space Win32 hands us in
/// <c>WM_WINDOWPOSCHANGING</c> / <c>GetWindowRect</c>. Staying in device pixels end-to-end keeps
/// the snap free of any DPI conversion, which is what makes it behave identically on a mixed-DPI
/// multi-monitor desktop.</summary>
public readonly record struct SnapRect(int Left, int Top, int Right, int Bottom)
{
    public static SnapRect FromSize(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

/// <summary>User-facing snap configuration, mirrored from
/// <see cref="WormholeDefaultsService"/>.</summary>
/// <param name="ToGrid">Quantise edges to a <paramref name="GridSizePx"/> lattice.</param>
/// <param name="GridSizePx">Grid step, in physical pixels.</param>
/// <param name="ToWormholes">Attract edges (and centre lines) to the other open wormholes.</param>
/// <param name="ToScreenEdges">Attract edges to the monitor work area.</param>
/// <param name="GapPx">Distance always kept between the snapped wormhole and what it snapped
/// onto. 0 = flush contact; 8 leaves an 8 px alley between neighbours and off the screen edge.</param>
/// <param name="ThresholdPx">How close an edge must get before it is pulled in.</param>
public sealed record SnapOptions(
    bool ToGrid = false,
    int GridSizePx = 16,
    bool ToWormholes = false,
    bool ToScreenEdges = false,
    int GapPx = 0,
    int ThresholdPx = 12)
{
    public bool AnyEnabled => ToGrid || ToWormholes || ToScreenEdges;
}

/// <summary>Position/size snapping for wormhole windows. Pure function over rectangles so the
/// rules can be unit-tested without a desktop: the window plumbing only has to supply the
/// proposed rect, the neighbours and the work area.
///
/// Order of precedence: the grid quantises first (it is a coarse lattice, always applied when
/// enabled), then a screen-edge / neighbour match within <see cref="SnapOptions.ThresholdPx"/>
/// overrides it — landing exactly against another wormhole matters more than staying on the
/// lattice, and the two would otherwise fight over the same few pixels.</summary>
public static class WormholeSnapEngine
{
    /// <summary>Adjust <paramref name="proposed"/> according to <paramref name="options"/>.
    /// Returns the rect unchanged when nothing is enabled or nothing is within reach.</summary>
    /// <param name="proposed">Rect Windows is about to apply.</param>
    /// <param name="edges">Edges the gesture is allowed to touch.</param>
    /// <param name="others">Rects of the other live wormholes (exclude the one being dragged).</param>
    /// <param name="workArea">Work area of the monitor the wormhole is on.</param>
    /// <param name="options">User settings.</param>
    /// <param name="minWidth">Lower bound enforced after a horizontal resize snap.</param>
    /// <param name="minHeight">Lower bound enforced after a vertical resize snap.</param>
    public static SnapRect Snap(
        SnapRect proposed,
        SnapEdges edges,
        IReadOnlyList<SnapRect> others,
        SnapRect workArea,
        SnapOptions options,
        int minWidth = 1,
        int minHeight = 1)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AnyEnabled || edges == SnapEdges.None) return proposed;

        return edges.HasFlag(SnapEdges.Move)
            ? SnapMove(proposed, others, workArea, options)
            : SnapResize(proposed, edges, others, workArea, options, minWidth, minHeight);
    }

    private static SnapRect SnapMove(SnapRect r, IReadOnlyList<SnapRect> others, SnapRect work, SnapOptions o)
    {
        var dx = BestDelta(
            Candidates(o, horizontal: true, r, others, work),
            new[] { r.Left, r.Right, r.CenterX },
            GridDelta(o, r.Left, work.Left),
            o.ThresholdPx);
        var dy = BestDelta(
            Candidates(o, horizontal: false, r, others, work),
            new[] { r.Top, r.Bottom, r.CenterY },
            GridDelta(o, r.Top, work.Top),
            o.ThresholdPx);

        return new SnapRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
    }

    private static SnapRect SnapResize(
        SnapRect r, SnapEdges edges, IReadOnlyList<SnapRect> others, SnapRect work, SnapOptions o,
        int minWidth, int minHeight)
    {
        var left = r.Left;
        var top = r.Top;
        var right = r.Right;
        var bottom = r.Bottom;

        // A resize moves one edge at a time, so each dragged edge gets its own delta computed
        // from the targets that make sense for THAT edge (a left edge snaps onto a neighbour's
        // right edge + gap, a right edge onto a neighbour's left edge - gap, and so on).
        if (edges.HasFlag(SnapEdges.Left))
            left += BestDelta(EdgeTargets(o, others, work, Side.Left), [r.Left], GridDelta(o, r.Left, work.Left), o.ThresholdPx);
        if (edges.HasFlag(SnapEdges.Right))
            right += BestDelta(EdgeTargets(o, others, work, Side.Right), [r.Right], GridDelta(o, r.Right, work.Left), o.ThresholdPx);
        if (edges.HasFlag(SnapEdges.Top))
            top += BestDelta(EdgeTargets(o, others, work, Side.Top), [r.Top], GridDelta(o, r.Top, work.Top), o.ThresholdPx);
        if (edges.HasFlag(SnapEdges.Bottom))
            bottom += BestDelta(EdgeTargets(o, others, work, Side.Bottom), [r.Bottom], GridDelta(o, r.Bottom, work.Top), o.ThresholdPx);

        // Never let a snap shrink the window past its minimum — the drag loop's own constraint
        // runs before us, so an unchecked snap here is the one thing that could violate it.
        if (right - left < minWidth)
        {
            if (edges.HasFlag(SnapEdges.Left)) left = right - minWidth;
            else right = left + minWidth;
        }
        if (bottom - top < minHeight)
        {
            if (edges.HasFlag(SnapEdges.Top)) top = bottom - minHeight;
            else bottom = top + minHeight;
        }
        return new SnapRect(left, top, right, bottom);
    }

    private enum Side { Left, Top, Right, Bottom }

    /// <summary>Targets a single dragged edge may land on.</summary>
    private static List<int> EdgeTargets(SnapOptions o, IReadOnlyList<SnapRect> others, SnapRect work, Side side)
    {
        var targets = new List<int>();
        if (o.ToScreenEdges)
        {
            targets.Add(side switch
            {
                Side.Left  => work.Left + o.GapPx,
                Side.Top   => work.Top + o.GapPx,
                Side.Right => work.Right - o.GapPx,
                _          => work.Bottom - o.GapPx,
            });
        }
        if (o.ToWormholes)
        {
            foreach (var other in others)
            {
                switch (side)
                {
                    case Side.Left:
                        targets.Add(other.Right + o.GapPx); // park against its right flank
                        targets.Add(other.Left);            // align left edges
                        break;
                    case Side.Right:
                        targets.Add(other.Left - o.GapPx);
                        targets.Add(other.Right);
                        break;
                    case Side.Top:
                        targets.Add(other.Bottom + o.GapPx);
                        targets.Add(other.Top);
                        break;
                    default:
                        targets.Add(other.Top - o.GapPx);
                        targets.Add(other.Bottom);
                        break;
                }
            }
        }
        return targets;
    }

    /// <summary>Per-edge target lists for a MOVE, keyed by the moving rect's own edges: index 0
    /// applies to the leading edge (left/top), 1 to the trailing edge (right/bottom), 2 to the
    /// centre line. Adjacency targets only count when the two rects overlap on the other axis —
    /// otherwise a wormhole parked in a far corner would still tug at this one — while plain
    /// edge alignment stays unconditional, which is what makes tidy columns/rows possible.</summary>
    private static List<int>[] Candidates(
        SnapOptions o, bool horizontal, SnapRect r, IReadOnlyList<SnapRect> others, SnapRect work)
    {
        var leading = new List<int>();
        var trailing = new List<int>();
        var centre = new List<int>();

        if (o.ToScreenEdges)
        {
            leading.Add(horizontal ? work.Left + o.GapPx : work.Top + o.GapPx);
            trailing.Add(horizontal ? work.Right - o.GapPx : work.Bottom - o.GapPx);
            centre.Add(horizontal ? work.CenterX : work.CenterY);
        }
        if (o.ToWormholes)
        {
            foreach (var other in others)
            {
                var overlaps = horizontal
                    ? r.Bottom > other.Top - o.ThresholdPx && r.Top < other.Bottom + o.ThresholdPx
                    : r.Right > other.Left - o.ThresholdPx && r.Left < other.Right + o.ThresholdPx;
                if (overlaps)
                {
                    leading.Add(horizontal ? other.Right + o.GapPx : other.Bottom + o.GapPx);
                    trailing.Add(horizontal ? other.Left - o.GapPx : other.Top - o.GapPx);
                }
                leading.Add(horizontal ? other.Left : other.Top);
                trailing.Add(horizontal ? other.Right : other.Bottom);
                centre.Add(horizontal ? other.CenterX : other.CenterY);
            }
        }
        return [leading, trailing, centre];
    }

    /// <summary>Delta that would put <paramref name="value"/> back on the lattice, or null when
    /// the grid is off. Origin is the work area so the lattice is anchored per monitor.</summary>
    private static int? GridDelta(SnapOptions o, int value, int origin)
    {
        if (!o.ToGrid || o.GridSizePx <= 1) return null;
        var offset = value - origin;
        var snapped = (int)Math.Round((double)offset / o.GridSizePx, MidpointRounding.AwayFromZero) * o.GridSizePx;
        return snapped + origin - value;
    }

    /// <summary>Pick the smallest in-threshold correction across every (edge, target) pair. The
    /// grid delta is the fallback used when nothing else is in reach.</summary>
    private static int BestDelta(IReadOnlyList<List<int>> targetsPerEdge, IReadOnlyList<int> edgeValues, int? gridDelta, int threshold)
    {
        var best = int.MaxValue;
        var found = false;
        for (var i = 0; i < edgeValues.Count && i < targetsPerEdge.Count; i++)
        {
            foreach (var target in targetsPerEdge[i])
            {
                var delta = target - edgeValues[i];
                if (Math.Abs(delta) > threshold) continue;
                if (!found || Math.Abs(delta) < Math.Abs(best)) { best = delta; found = true; }
            }
        }
        if (found) return best;
        return gridDelta ?? 0;
    }

    private static int BestDelta(List<int> targets, IReadOnlyList<int> edgeValues, int? gridDelta, int threshold)
        => BestDelta([targets], edgeValues, gridDelta, threshold);

    /// <summary>Work out which edges a gesture is dragging by diffing the rect Windows proposes
    /// against the one the move/size loop started from. Same size = the window is travelling;
    /// otherwise the edges that moved are the ones under the mouse.</summary>
    public static SnapEdges DetectEdges(SnapRect start, SnapRect proposed)
    {
        if (start.Width == proposed.Width && start.Height == proposed.Height) return SnapEdges.Move;
        var edges = SnapEdges.None;
        if (proposed.Left != start.Left) edges |= SnapEdges.Left;
        if (proposed.Top != start.Top) edges |= SnapEdges.Top;
        if (proposed.Right != start.Right) edges |= SnapEdges.Right;
        if (proposed.Bottom != start.Bottom) edges |= SnapEdges.Bottom;
        return edges;
    }
}
