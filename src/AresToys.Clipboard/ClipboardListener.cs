using AresToys.Clipboard.Native;

namespace AresToys.Clipboard;

public sealed class ClipboardListener : IClipboardListener
{
    /// <summary>Time-window the listener stays muted after a SuppressNext / SuppressFor call.
    /// Single-shot bool wasn't enough: a workflow with two back-to-back Copy-image-to-clipboard
    /// steps (original + post-effects bytes) produces two SetImage / WM_CLIPBOARDUPDATE pairs
    /// that interleave with each other — the bool would only swallow one and the second
    /// re-ingested as a phantom "[image]" entry. 500 ms covers any sane sequence of in-app
    /// writes without affecting genuine user-driven clipboard activity.</summary>
    private static readonly TimeSpan DefaultSuppressionWindow = TimeSpan.FromMilliseconds(500);

    private IntPtr _hwnd;
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    public event EventHandler? ClipboardUpdated;

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("Handle cannot be zero.", nameof(windowHandle));
        if (_hwnd != IntPtr.Zero) throw new InvalidOperationException("ClipboardListener is already attached.");
        if (!ClipboardNativeMethods.AddClipboardFormatListener(windowHandle))
            throw new InvalidOperationException("AddClipboardFormatListener failed.");
        _hwnd = windowHandle;
    }

    public bool OnWindowMessage(int message)
    {
        if (message != ClipboardNativeMethods.WmClipboardUpdate) return false;
        if (DateTimeOffset.UtcNow < _suppressUntil)
        {
            return true;
        }
        ClipboardUpdated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SuppressNext()
    {
        // "Next" historically meant "the very next event", but pipelines that issue multiple
        // self-writes (e.g. Copy image to clipboard called twice with different bytes) need
        // every WM_CLIPBOARDUPDATE in the burst muted, not just the first. Treat SuppressNext
        // as "mute for the default window" — additional calls extend the window further.
        var until = DateTimeOffset.UtcNow + DefaultSuppressionWindow;
        if (until > _suppressUntil) _suppressUntil = until;
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        ClipboardNativeMethods.RemoveClipboardFormatListener(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
