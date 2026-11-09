namespace AresToys.App.Services.Zones;

/// <summary>A named layout, either a grid or (when <see cref="Canvas"/> is set) a canvas of free rectangles.
/// Every layout is a saved, editable layout. A canvas layout carries <see cref="NoGrid"/>
/// in <see cref="Grid"/>, which fails validation on purpose: read per-layout values through the members below.</summary>
public sealed record ZoneLayout(string Id, string Name, GridLayout Grid, CanvasLayout? Canvas = null)
{
    /// <summary>Placeholder grid of a canvas layout. Invalid on purpose, so a wrong read of Grid fails loudly.</summary>
    public static GridLayout NoGrid { get; } = new([], [], [], 0);

    public bool IsCanvas => Canvas is not null;

    public int ZoneCount => Canvas?.ZoneCount ?? Grid.ZoneCount;

    /// <summary>Gap between zones in pixels; canvas layouts have none.</summary>
    public int Spacing => Canvas is null ? Grid.Spacing : 0;

    public int SensitivityRadius => Canvas?.SensitivityRadius ?? Grid.SensitivityRadius;

    public static ZoneLayout ForCanvas(string id, string name, CanvasLayout canvas) => new(id, name, NoGrid, canvas);

    /// <summary>Returns an error message, or null when the layout is usable.</summary>
    public static string? Validate(ZoneLayout layout)
        => layout.Canvas is { } canvas ? CanvasLayout.Validate(canvas) : GridLayout.Validate(layout.Grid);
}
