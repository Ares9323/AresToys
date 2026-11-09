namespace AresToys.ImageEffects.Drawing;

/// <summary>4-sided integer padding/margin (Left, Top, Right, Bottom). Round-trips with the
/// ShareX <c>System.Windows.Forms.Padding</c> string format <c>"L, T, R, B"</c> via
/// <see cref="AresToys.ImageEffects.Serialization.PaddingJsonConverter"/>.</summary>
public readonly record struct Padding(int Left, int Top, int Right, int Bottom)
{
    public static Padding Empty => default;
    public int Horizontal => Left + Right;
    public int Vertical => Top + Bottom;

    public override string ToString() => $"{Left}, {Top}, {Right}, {Bottom}";
}
