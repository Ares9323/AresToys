namespace AresToys.App.Services.Zones;

/// <summary>Direction of a grid line. Vertical is a line that separates left from right (its position is
/// measured along X, it lives between two columns). Horizontal separates top from bottom (position along Y,
/// between two rows).</summary>
public enum ZoneAxis
{
    Vertical,
    Horizontal,
}
