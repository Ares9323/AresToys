namespace AresToys.App.Services.Zones;

/// <summary>Rectangle in device pixels. Half open: Left and Top are inside, Right and Bottom are not.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>Area in pixels, never negative (an inverted rect has area 0).</summary>
    public long Area => (long)Math.Max(Width, 0) * Math.Max(Height, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
