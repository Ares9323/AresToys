namespace AresToys.App.Services.Zones;

/// <summary>Grid factories for the editor's New menu, ported from PowerToys LayoutConfigurator (Columns). No layout is
/// built in: the menu offers an empty grid, a custom grid and a free-form start, and the owner duplicates a layout for the rest.</summary>
public static class ZoneLayoutPresets
{
    /// <summary>n equal columns (n 1..10).</summary>
    public static GridLayout Columns(int n)
    {
        n = Clamp(n);
        return new GridLayout([GridLayout.PercentTotal], Split(n), [Enumerable.Range(0, n).ToArray()], 0);
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
