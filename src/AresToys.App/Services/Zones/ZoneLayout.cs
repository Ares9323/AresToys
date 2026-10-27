namespace AresToys.App.Services.Zones;

/// <summary>A named grid layout. Built-in layouts are shipped with the app and never persisted.</summary>
public sealed record ZoneLayout(string Id, string Name, GridLayout Grid, bool IsBuiltIn);
