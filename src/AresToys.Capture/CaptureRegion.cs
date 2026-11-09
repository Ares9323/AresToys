namespace AresToys.Capture;

/// <summary>A rectangle in virtual-screen coordinates. <see cref="Width"/> and <see cref="Height"/> are positive.
/// <see cref="WindowTitle"/> is set when the region was snapped to a window (used to enrich the saved filename).</summary>
public sealed record CaptureRegion(int X, int Y, int Width, int Height, string? WindowTitle = null)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
