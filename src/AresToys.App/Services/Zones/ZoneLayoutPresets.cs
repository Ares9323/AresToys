namespace AresToys.App.Services.Zones;

/// <summary>Built-in grid layouts, ported from PowerToys LayoutConfigurator (Columns, Rows, Grid, PriorityGrid).</summary>
public static class ZoneLayoutPresets
{
    private static readonly (int[] Cols, int[] RowPercents, int[][] Map)[] PriorityTable =
    [
        ([10000], [10000], [[0]]),
        ([6667, 3333], [10000], [[0, 1]]),
        ([2500, 5000, 2500], [10000], [[0, 1, 2]]),
        ([2500, 5000, 2500], [5000, 5000], [[0, 1, 2], [0, 1, 3]]),
        ([2500, 5000, 2500], [5000, 5000], [[0, 1, 2], [3, 1, 4]]),
        ([2500, 5000, 2500], [3333, 3334, 3333], [[0, 1, 2], [0, 1, 3], [4, 1, 5]]),
        ([2500, 5000, 2500], [3333, 3334, 3333], [[0, 1, 2], [3, 1, 4], [5, 1, 6]]),
        ([2500, 2500, 2500, 2500], [3333, 3334, 3333], [[0, 1, 2, 3], [4, 1, 2, 5], [6, 1, 2, 7]]),
        ([2500, 2500, 2500, 2500], [3333, 3334, 3333], [[0, 1, 2, 3], [4, 1, 2, 5], [6, 1, 7, 8]]),
        ([2500, 2500, 2500, 2500], [3333, 3334, 3333], [[0, 1, 2, 3], [4, 1, 5, 6], [7, 1, 8, 9]]),
    ];

    /// <summary>The layouts shipped with the app, with stable ids.</summary>
    public static IReadOnlyList<ZoneLayout> BuiltIn { get; } =
    [
        new("preset-columns-2", "Two columns", Columns(2), true),
        new("preset-columns-3", "Three columns", Columns(3), true),
        new("preset-rows-2", "Two rows", Rows(2), true),
        new("preset-grid-4", "Grid 2 x 2", Grid(4), true),
        new("preset-priority-3", "Priority grid 3", PriorityGrid(3), true),
        new("preset-priority-5", "Priority grid 5", PriorityGrid(5), true),
    ];

    /// <summary>n equal columns (n 1..10).</summary>
    public static GridLayout Columns(int n)
    {
        n = Clamp(n);
        return new GridLayout([GridLayout.PercentTotal], Split(n), [Enumerable.Range(0, n).ToArray()], 0);
    }

    /// <summary>n equal rows (n 1..10).</summary>
    public static GridLayout Rows(int n)
    {
        n = Clamp(n);
        return new GridLayout(Split(n), [GridLayout.PercentTotal],
            Enumerable.Range(0, n).Select(i => (IReadOnlyList<int>)new[] { i }).ToArray(), 0);
    }

    /// <summary>Near square grid. Rows is the largest r with r*r &lt;= n; when n does not fill the grid the last
    /// zone absorbs the leftover cells (Grid(3) is one row of three, Grid(5) is 2 x 3 with a wide last zone).</summary>
    public static GridLayout Grid(int n)
    {
        n = Clamp(n);
        int rows = 1;
        while (n / rows >= rows) rows++;
        rows--;
        int cols = n / rows + (n % rows == 0 ? 0 : 1);

        var map = new IReadOnlyList<int>[rows];
        int index = 0;
        for (int r = 0; r < rows; r++)
        {
            var row = new int[cols];
            for (int c = 0; c < cols; c++)
            {
                row[c] = index++;
                if (index == n) index--;
            }

            map[r] = row;
        }

        return new GridLayout(Split(rows), Split(cols), map, 0);
    }

    /// <summary>Largest row or column count <see cref="GridOf"/> accepts.</summary>
    public const int MaxGridLines = 12;

    /// <summary>rows x columns equal cells, each its own zone numbered row by row. Both counts are clamped to
    /// 1..<see cref="MaxGridLines"/>; the percents add up to exactly 10000 (a leftover of one unit lands on some lines).</summary>
    public static GridLayout GridOf(int rows, int columns)
    {
        rows = Math.Clamp(rows, 1, MaxGridLines);
        columns = Math.Clamp(columns, 1, MaxGridLines);
        var map = new IReadOnlyList<int>[rows];
        for (int r = 0; r < rows; r++)
        {
            map[r] = Enumerable.Range(r * columns, columns).ToArray();
        }

        return new GridLayout(Split(rows), Split(columns), map, 0);
    }

    /// <summary>Hand made PowerToys priority grid (a large central zone). n 1..10.</summary>
    public static GridLayout PriorityGrid(int n)
    {
        var (cols, rowPercents, map) = PriorityTable[Clamp(n) - 1];
        return new GridLayout(rowPercents, cols, map.Select(r => (IReadOnlyList<int>)r).ToArray(), 0);
    }

    private static int Clamp(int n) => Math.Clamp(n, 1, 10);

    // Percents that add up to exactly 10000 (same expression as PowerToys).
    private static int[] Split(int count)
    {
        var parts = new int[count];
        for (int i = 0; i < count; i++)
            parts[i] = GridLayout.PercentTotal * (i + 1) / count - GridLayout.PercentTotal * i / count;
        return parts;
    }
}
