namespace AresToys.Editor.Model;

/// <summary>ARGB color model used by editor shapes. <see cref="A"/> 0 = transparent.</summary>
public sealed record ShapeColor(byte A, byte R, byte G, byte B)
{
    public static readonly ShapeColor Transparent = new(0, 0, 0, 0);
    public static readonly ShapeColor Red = new(255, 220, 20, 60);
    public static readonly ShapeColor Black = new(255, 0, 0, 0);

    public bool IsTransparent => A == 0;
}
