namespace AresToys.Clipboard;

/// <summary>The foreground window's owner process and title, taken once at the moment of a
/// clipboard event so the gate and the reader judge the same window.</summary>
public sealed record ForegroundSnapshot(string? ProcessName, string? WindowTitle);
