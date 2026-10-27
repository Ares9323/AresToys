namespace AresToys.App.Services.Zones;

/// <summary>Turns a grid layout into zone rectangles for a work area. Port of PowerToys CalculateGridZones.</summary>
public static class ZoneGeometry
{
    /// <summary>Bounding rectangle of the zones at <paramref name="indexes"/>; an empty rect when none is valid.</summary>
    public static PixelRect Union(IReadOnlyList<PixelRect> zones, IReadOnlyCollection<int> indexes)
    {
        var any = false;
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (var i in indexes)
        {
            if (i < 0 || i >= zones.Count || zones[i].IsEmpty) continue;
            var z = zones[i];
            if (!any)
            {
                (left, top, right, bottom) = (z.Left, z.Top, z.Right, z.Bottom);
                any = true;
            }
            else
            {
                left = Math.Min(left, z.Left);
                top = Math.Min(top, z.Top);
                right = Math.Max(right, z.Right);
                bottom = Math.Max(bottom, z.Bottom);
            }
        }

        return any ? new PixelRect(left, top, right, bottom) : new PixelRect(0, 0, 0, 0);
    }

    /// <summary>Zone rectangles indexed by zone index, in the coordinates of <paramref name="workArea"/>.
    /// Empty when the layout is invalid or the area is empty.</summary>
    public static IReadOnlyList<PixelRect> Compute(GridLayout layout, PixelRect workArea)
    {
        if (workArea.IsEmpty || GridLayout.Validate(layout) is not null) return [];

        int rows = layout.RowPercents.Count;
        int cols = layout.ColumnPercents.Count;
        var rowStart = new int[rows];
        var rowEnd = new int[rows];
        var colStart = new int[cols];
        var colEnd = new int[cols];

        // Cumulative percent times total keeps the sum of all extents exactly equal to the total.
        long acc = 0;
        for (int r = 0; r < rows; r++)
        {
            rowStart[r] = (int)(acc * workArea.Height / GridLayout.PercentTotal);
            acc += layout.RowPercents[r];
            rowEnd[r] = (int)(acc * workArea.Height / GridLayout.PercentTotal);
        }

        acc = 0;
        for (int c = 0; c < cols; c++)
        {
            colStart[c] = (int)(acc * workArea.Width / GridLayout.PercentTotal);
            acc += layout.ColumnPercents[c];
            colEnd[c] = (int)(acc * workArea.Width / GridLayout.PercentTotal);
        }

        var map = layout.CellMap;
        int spacing = layout.Spacing;
        var zones = new PixelRect[layout.ZoneCount];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int id = map[r][c];
                bool topLeft = (r == 0 || map[r - 1][c] != id) && (c == 0 || map[r][c - 1] != id);
                if (!topLeft) continue;

                int maxRow = r;
                while (maxRow + 1 < rows && map[maxRow + 1][c] == id) maxRow++;
                int maxCol = c;
                while (maxCol + 1 < cols && map[r][maxCol + 1] == id) maxCol++;

                int left = colStart[c] + (c == 0 ? spacing : spacing / 2);
                int top = rowStart[r] + (r == 0 ? spacing : spacing / 2);
                int right = colEnd[maxCol] - (maxCol == cols - 1 ? spacing : spacing / 2);
                int bottom = rowEnd[maxRow] - (maxRow == rows - 1 ? spacing : spacing / 2);

                // Spacing larger than a zone inverts it: like PowerToys, reject the whole set.
                if (right < left || bottom < top) return [];

                zones[id] = new PixelRect(workArea.Left + left, workArea.Top + top, workArea.Left + right, workArea.Top + bottom);
            }
        }

        return zones;
    }
}
