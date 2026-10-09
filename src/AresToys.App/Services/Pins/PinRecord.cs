namespace AresToys.App.Services.Pins;

public enum PinKind
{
    Image,
    Video,
}

/// <summary>One pinned window as persisted in <c>Pins\pins.json</c>. Geometry is in physical
/// screen pixels (the window's outer rectangle, border included) so a restore lands on the exact
/// same pixels at any monitor scaling; the DIP size is rebuilt from <see cref="DpiScaleX"/> /
/// <see cref="DpiScaleY"/>, the DPI the window computed its size with when it was created.</summary>
public sealed class PinRecord
{
    public Guid Id { get; set; }

    public PinKind Kind { get; set; }

    /// <summary>Image pins: file name of the lossless PNG inside the Pins folder.</summary>
    public string? FileName { get; set; }

    /// <summary>Video pins: absolute path of the video / GIF on disk (never copied).</summary>
    public string? SourcePath { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>User zoom (Ctrl+wheel), 1.0 = one bitmap pixel per physical pixel.</summary>
    public double Scale { get; set; } = 1.0;

    public double Opacity { get; set; } = 1.0;

    /// <summary>Border thickness in DIPs.</summary>
    public int Border { get; set; }

    public double DpiScaleX { get; set; } = 1.0;
    public double DpiScaleY { get; set; } = 1.0;

    /// <summary>Locked pins can't be moved, resized or closed until unlocked.</summary>
    public bool Locked { get; set; }

    /// <summary>Video pins only.</summary>
    public bool Muted { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public PinRecord Clone() => (PinRecord)MemberwiseClone();
}

/// <summary>Root object of <c>pins.json</c>.</summary>
public sealed class PinManifest
{
    public int SchemaVersion { get; set; } = 1;

    public List<PinRecord> Pins { get; set; } = new();
}
