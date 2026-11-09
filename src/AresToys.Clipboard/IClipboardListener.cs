namespace AresToys.Clipboard;

public interface IClipboardListener : IDisposable
{
    event EventHandler? ClipboardUpdated;

    /// <summary>Bind to the message-pump window handle. Must be called once.</summary>
    void Attach(IntPtr windowHandle);

    /// <summary>Forward a window message; returns true if the message was a clipboard-update.</summary>
    bool OnWindowMessage(int message);

    /// <summary>Suppress (drop) the next clipboard-update event. Used when the app itself sets the clipboard
    /// and doesn't want to re-ingest its own write. Self-resets after one event.</summary>
    void SuppressNext();
}
