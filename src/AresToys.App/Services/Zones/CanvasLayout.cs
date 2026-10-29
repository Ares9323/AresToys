namespace AresToys.App.Services.Zones;

/// <summary>One free rectangle of a canvas layout, in 1/10000 of the work area.</summary>
public sealed record CanvasZone(int Left, int Top, int Right, int Bottom);

/// <summary>Canvas zone layout: free rectangles that may overlap, in the order they were drawn. Coordinates are in
/// 1/10000 of the work area (<see cref="GridLayout.PercentTotal"/>); the sensitivity radius is in device pixels.</summary>
public sealed record CanvasLayout(
    IReadOnlyList<CanvasZone> Zones,
    int SensitivityRadius = GridLayout.DefaultSensitivityRadius)
{
    public const int MaxZones = 64;

    /// <summary>Smallest width or height of a zone: 1% of the work area.</summary>
    public const int MinSize = 100;

    public int ZoneCount => Zones.Count;

    /// <summary>Returns an error message, or null when the layout is usable.</summary>
    public static string? Validate(CanvasLayout layout)
    {
        if (layout is null) return "Layout is missing.";
        if (layout.Zones is null || layout.Zones.Count == 0) return "Layout needs at least one zone.";
        if (layout.Zones.Count > MaxZones) return $"Layout cannot have more than {MaxZones} zones.";
        for (int i = 0; i < layout.Zones.Count; i++)
        {
            var z = layout.Zones[i];
            if (z is null || z.Left < 0 || z.Top < 0 || z.Right > GridLayout.PercentTotal || z.Bottom > GridLayout.PercentTotal
                || z.Right - z.Left < MinSize || z.Bottom - z.Top < MinSize)
                return $"Zone {i + 1} is outside the work area or too small.";
        }

        if (layout.SensitivityRadius < 0) return "Sensitivity radius cannot be negative.";
        return null;
    }
}
