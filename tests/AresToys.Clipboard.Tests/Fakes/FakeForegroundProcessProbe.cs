namespace AresToys.Clipboard.Tests.Fakes;

internal sealed class FakeForegroundProcessProbe : IForegroundProcessProbe
{
    public string? ProcessName { get; set; }
    public string? WindowTitle { get; set; }
    public string? GetForegroundProcessName() => ProcessName;
    public string? GetForegroundWindowTitle() => WindowTitle;
}
