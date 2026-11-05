namespace AresToys.Clipboard;

public interface IClipboardReader
{
    /// <summary>Read whatever is currently on the clipboard. Returns null if the clipboard is empty,
    /// unreadable, or holds something over the reader's size limits. Safe to call from a worker thread:
    /// the foreground window and the capture time are passed in because they belong to the moment of
    /// the clipboard event, not to the moment of the read.</summary>
    ClipboardChange? ReadCurrent(IntPtr ownerHwnd, ForegroundSnapshot foreground, DateTimeOffset capturedAt);
}
