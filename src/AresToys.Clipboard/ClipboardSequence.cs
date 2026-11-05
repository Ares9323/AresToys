using AresToys.Clipboard.Native;

namespace AresToys.Clipboard;

/// <summary>The system clipboard sequence number: it grows on every change of the clipboard
/// contents, so a queued read can tell that a newer copy has replaced the one it was queued for.</summary>
public static class ClipboardSequence
{
    public static uint Current => ClipboardNativeMethods.GetClipboardSequenceNumber();
}
