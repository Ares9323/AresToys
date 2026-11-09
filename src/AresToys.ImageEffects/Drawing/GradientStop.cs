using SkiaSharp;

namespace AresToys.ImageEffects.Drawing;

/// <summary>One stop in a linear gradient. <see cref="Location"/> is in 0..100 percent
/// (matches ShareX <c>GradientStop.Location</c>); <see cref="Color"/> uses
/// <see cref="SKColor"/> so renderers can pass it straight to Skia.</summary>
public sealed class GradientStop
{
    public SKColor Color { get; set; } = SKColors.Black;
    public float Location { get; set; }

    public GradientStop() { }
    public GradientStop(SKColor color, float location)
    {
        Color = color;
        Location = location;
    }
}
