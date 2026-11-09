namespace AresToys.Capture;

/// <summary>Result of a successful capture: bitmap encoded as PNG with its dimensions for downstream consumers.</summary>
public sealed record CapturedImage(int Width, int Height, byte[] PngBytes);
