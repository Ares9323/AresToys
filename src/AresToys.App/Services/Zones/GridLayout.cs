namespace AresToys.App.Services.Zones;

/// <summary>Grid zone layout, same model as the PowerToys grid layout. Percents are in 1/10000 and each list
/// sums to 10000. <see cref="CellMap"/> is rows x columns of zone indexes; cells sharing an index form one zone,
/// which must be a rectangle. Spacing and sensitivity radius are in device pixels.</summary>
public sealed record GridLayout(
    IReadOnlyList<int> RowPercents,
    IReadOnlyList<int> ColumnPercents,
    IReadOnlyList<IReadOnlyList<int>> CellMap,
    int Spacing,
    int SensitivityRadius = GridLayout.DefaultSensitivityRadius)
{
    public const int DefaultSensitivityRadius = 20;
    public const int PercentTotal = 10000;

    /// <summary>Number of zones (highest index + 1), or 0 when the map is empty.</summary>
    public int ZoneCount => CellMap.Count == 0 ? 0 : CellMap.Max(r => r.Count == 0 ? -1 : r.Max()) + 1;

    /// <summary>Returns an error message, or null when the layout is usable.</summary>
    public static string? Validate(GridLayout layout)
    {
        if (layout is null) return "Layout is missing.";
        if (layout.RowPercents is null || layout.ColumnPercents is null || layout.CellMap is null)
            return "Layout has missing parts.";
        int rows = layout.RowPercents.Count;
        int cols = layout.ColumnPercents.Count;
        if (rows == 0 || cols == 0) return "Layout needs at least one row and one column.";
        if (layout.RowPercents.Any(p => p <= 0) || layout.ColumnPercents.Any(p => p <= 0))
            return "Every row and column needs a positive size.";
        if (layout.RowPercents.Sum() != PercentTotal) return "Row sizes must add up to 100%.";
        if (layout.ColumnPercents.Sum() != PercentTotal) return "Column sizes must add up to 100%.";
        if (layout.Spacing < 0) return "Spacing cannot be negative.";
        if (layout.SensitivityRadius < 0) return "Sensitivity radius cannot be negative.";
        if (layout.CellMap.Count != rows || layout.CellMap.Any(r => r is null || r.Count != cols))
            return "Cell map does not match the rows and columns.";

        int max = -1;
        foreach (var row in layout.CellMap)
        {
            foreach (int id in row)
            {
                if (id < 0) return "Zone indexes cannot be negative.";
                if (id > max) max = id;
            }
        }

        // Per zone: bounding box of its cells, then the box must hold exactly as many cells as the zone has.
        var minRow = new int[max + 1];
        var maxRow = new int[max + 1];
        var minCol = new int[max + 1];
        var maxCol = new int[max + 1];
        var cellCount = new int[max + 1];
        Array.Fill(minRow, int.MaxValue);
        Array.Fill(minCol, int.MaxValue);
        Array.Fill(maxRow, -1);
        Array.Fill(maxCol, -1);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int id = layout.CellMap[r][c];
                cellCount[id]++;
                minRow[id] = Math.Min(minRow[id], r);
                maxRow[id] = Math.Max(maxRow[id], r);
                minCol[id] = Math.Min(minCol[id], c);
                maxCol[id] = Math.Max(maxCol[id], c);
            }
        }

        for (int id = 0; id <= max; id++)
        {
            if (cellCount[id] == 0) return "Zone indexes must be contiguous.";
            int boxCells = (maxRow[id] - minRow[id] + 1) * (maxCol[id] - minCol[id] + 1);
            if (boxCells != cellCount[id]) return $"Zone {id + 1} is not a rectangle.";
        }

        return null;
    }
}
