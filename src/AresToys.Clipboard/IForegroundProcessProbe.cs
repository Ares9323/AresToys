namespace AresToys.Clipboard;

public interface IForegroundProcessProbe
{
    /// <summary>Returns the executable name (e.g. "chrome.exe") of the foreground window's owning process, or null on failure.</summary>
    string? GetForegroundProcessName();

    /// <summary>Returns the title of the foreground window, or null on failure.</summary>
    string? GetForegroundWindowTitle();

    /// <summary>Process name and title of the foreground window in one go. The real probe overrides
    /// this to look the window up once; the default composes the two calls above.</summary>
    ForegroundSnapshot GetForeground() => new(GetForegroundProcessName(), GetForegroundWindowTitle());
}
