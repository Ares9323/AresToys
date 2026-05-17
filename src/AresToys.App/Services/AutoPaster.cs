using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;
using AresToys.Clipboard;
using AresToys.Core.Domain;
using AresToys.Storage.Items;

namespace AresToys.App.Services;

public sealed class AutoPaster
{
    private readonly IItemStore _items;
    private readonly TargetWindowTracker _target;
    private readonly IClipboardListener? _listener;
    private readonly ILogger<AutoPaster> _logger;

    public AutoPaster(IItemStore items, TargetWindowTracker target, ILogger<AutoPaster> logger, IClipboardListener? listener = null)
    {
        _items = items;
        _target = target;
        _logger = logger;
        // Optional — when the clipboard module is disabled (Settings → Modules) the listener
        // singleton isn't registered. We still want the paste to work; just skip the
        // SuppressNext call and accept the round-trip ingestion in that edge case.
        _listener = listener;
    }

    public Task PasteAsync(long itemId, CancellationToken cancellationToken)
        => PasteAsync(itemId, restoreForeground: true, cancellationToken);

    /// <summary>"Paste the path of the file behind this item as plain text". Resolves the file path
    /// from BlobRef (Image/Video saved on disk, Files entries) or from a Files entry's path list
    /// payload. When a valid on-disk path is found, pushes it as text + sends Ctrl+V. Falls back
    /// to the regular <see cref="PasteAsync(long, CancellationToken)"/> when no path is available
    /// (Text-only items, deleted file, etc.) so the user gets the expected paste either way.
    /// Bound to Shift+Enter in the popup.</summary>
    public async Task PastePathAsTextAsync(long itemId, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null) return;

        // Path resolution: BlobRef first (canonical for Image / Video / Save-as-Image-file), then
        // the Files payload (newline-joined path list from CF_HDROP). We pick the first path that
        // actually exists on disk — a stale entry whose source file was deleted falls through to
        // the normal paste path so the user doesn't end up with a dead path on the clipboard.
        string? path = null;
        if (!string.IsNullOrEmpty(record.BlobRef) && System.IO.File.Exists(record.BlobRef))
        {
            path = record.BlobRef;
        }
        else if (record.Kind == ItemKind.Files)
        {
            foreach (var p in Encoding.UTF8.GetString(record.Payload.Span)
                                          .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (System.IO.File.Exists(p)) { path = p; break; }
            }
        }

        if (path is null)
        {
            _logger.LogDebug("AutoPaster.PastePathAsText: item {Id} has no resolvable on-disk path — falling back to regular paste.", itemId);
            await PasteAsync(itemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Same flow as the regular PasteAsync image / text branches: dispatch the clipboard write
        // onto the UI thread (Suppress the listener so it doesn't echo back as a new history row),
        // restore foreground, settle, release any held modifiers, send Ctrl+V.
        var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _listener?.SuppressNext();
            try { System.Windows.Clipboard.SetText(path); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoPaster.PastePathAsText: SetText failed for {Path}", path);
                return false;
            }
            var restoreOk = _target.TryRestoreCaptured();
            _logger.LogDebug("AutoPaster.PastePathAsText: TryRestoreCaptured returned {Ok}", restoreOk);
            return restoreOk;
        });
        if (!ok) return;

        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        var released = KeyInjector.ReleaseStickyModifiers();
        if (released > 0) await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        SendCtrlV();
    }

    /// <summary>Paste an item with explicit control over whether to force-restore the previously
    /// captured foreground window. The default <c>true</c> matches the historical popup-driven
    /// flow: the popup window stole focus when the user clicked, so we have to push it back to
    /// the target via <c>SetForegroundWindow</c> + the Alt-tap unlock trick before sending
    /// Ctrl+V. Pass <c>false</c> when the caller's UI never stole focus to begin with
    /// (e.g. the KeySequences overlay, which is <c>WS_EX_NOACTIVATE</c>): the Alt tap there
    /// triggers Chrome / Edge to open the menu bar, eating the subsequent Ctrl+V.</summary>
    public async Task PasteAsync(long itemId, bool restoreForeground, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null) return;

        // Apply clipboard + restore foreground synchronously on the UI thread to keep
        // the foreground state consistent. SendInput then fires after a short settle delay.
        var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Tell the clipboard listener to drop the next ingestion — without this our own
            // SetText / SetPng below would echo back through Win32ClipboardReader and land as
            // a NEW item in the history every time the user pastes, producing a duplicate
            // entry on every Ctrl+V / hotkey paste.
            _listener?.SuppressNext();

            switch (record.Kind)
            {
                case ItemKind.Text:
                    var plainText = Encoding.UTF8.GetString(record.Payload.Span);
                    System.Windows.Clipboard.SetText(plainText);
                    break;

                case ItemKind.Html:
                    // Paste source resolution:
                    //  1. SearchText, when present AND not truncated (no trailing "…"). This is the
                    //     CF_UNICODETEXT the browser / app put alongside the rich HTML — for things
                    //     like "copy URL from address bar" the OS clipboard carries the URL as
                    //     UNICODETEXT and "<a href=...>Page title</a>" as HTML. Stripping the HTML
                    //     gives back "Page title" which is NOT what the user copied.
                    //  2. Strip HTML from the full payload otherwise — for long HTML payloads
                    //     (ASCII art, pre-formatted text blocks) we need the live extraction so
                    //     the paste isn't cut at 256 char with a "…" suffix.
                    //  3. SearchText as last resort when stripping yielded nothing.
                    string htmlPlain;
                    if (!string.IsNullOrEmpty(record.SearchText)
                        && !record.SearchText.EndsWith('…'))
                    {
                        htmlPlain = record.SearchText;
                    }
                    else
                    {
                        var htmlPayload = Encoding.UTF8.GetString(record.Payload.Span);
                        htmlPlain = ClipboardCleaning.HtmlToPlain(htmlPayload);
                        if (string.IsNullOrEmpty(htmlPlain) && !string.IsNullOrEmpty(record.SearchText))
                            htmlPlain = record.SearchText;
                    }
                    System.Windows.Clipboard.SetText(htmlPlain);
                    break;

                case ItemKind.Rtf:
                    // Same rationale as the HTML branch above — run the live RTF stripper on
                    // the full payload so long RTF documents don't paste with the SearchText
                    // truncation suffix.
                    var rtfPayload = Encoding.UTF8.GetString(record.Payload.Span);
                    var rtfPlain = ClipboardCleaning.RtfToPlain(rtfPayload);
                    if (string.IsNullOrEmpty(rtfPlain) && !string.IsNullOrEmpty(record.SearchText))
                        rtfPlain = record.SearchText;
                    System.Windows.Clipboard.SetText(rtfPlain);
                    break;

                case ItemKind.Image:
                    var pngBytes = record.Payload.ToArray();
                    _logger.LogDebug("AutoPaster image: {Length} bytes, first={B0:X2} {B1:X2} {B2:X2} {B3:X2}",
                        pngBytes.Length,
                        pngBytes.Length > 0 ? pngBytes[0] : (byte)0,
                        pngBytes.Length > 1 ? pngBytes[1] : (byte)0,
                        pngBytes.Length > 2 ? pngBytes[2] : (byte)0,
                        pngBytes.Length > 3 ? pngBytes[3] : (byte)0);
                    if (pngBytes.Length == 0) return false;
                    try
                    {
                        // PNG-aware publish (preserves alpha for Telegram / Discord / browsers).
                        // SetImage alone publishes only CF_BITMAP and modern apps interpret it
                        // as no-alpha — semi-transparent pixels render opaque on paste.
                        ClipboardImagePublisher.SetPng(pngBytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AutoPaster image decode/clipboard set failed");
                        return false;
                    }
                    break;

                case ItemKind.Video:
                    // Videos live on disk (BlobRef points at the .mp4 / .gif). Paste as a file
                    // drop (CF_HDROP) so Ctrl+V in Explorer / Telegram / Discord / email inserts
                    // the actual file rather than its serialized bytes. Without this Video items
                    // appeared in the popup but the Enter / Ctrl+N paste shortcut did nothing.
                    if (string.IsNullOrEmpty(record.BlobRef) || !System.IO.File.Exists(record.BlobRef))
                    {
                        _logger.LogWarning("AutoPaster video: BlobRef missing or file not found ('{Path}')", record.BlobRef);
                        return false;
                    }
                    var videoFiles = new System.Collections.Specialized.StringCollection { record.BlobRef };
                    try { System.Windows.Clipboard.SetFileDropList(videoFiles); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AutoPaster video: SetFileDropList failed");
                        return false;
                    }
                    break;

                default:
                    return false; // Files / unknown kinds: paste not supported yet
            }

            return true;
        });

        if (!ok) return;

        // Own-process WPF target: skip the SendInput Ctrl+V round-trip and dispatch
        // ApplicationCommands.Paste straight to the focused element. SendInput across the
        // foreground swap is racy for our own dialogs (Enter still held / repeated as the swap
        // happens, IsDefault buttons getting triggered, focus not yet pinned to a TextBox at
        // SendCtrlV time). Programmatic paste is deterministic — we bring the target window to
        // the front, push its focused TextBox-like control, and invoke the routed Paste command
        // on it. Falls back to the SendInput path when the focused element doesn't accept Paste.
        if (restoreForeground && _target.CapturedIsOwnProcess)
        {
            var done = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Same-process target: bring the WPF Window to the foreground via the framework's
                // own Activate() instead of TargetWindowTracker.TryRestoreCaptured. The latter
                // injects an Alt-tap to bypass Win32 focus-stealing rules, but on same-process
                // that's both unnecessary AND harmful — the Alt-down/Alt-up enters the system
                // "Alt menu activation pending" state, which makes the NEXT key the user presses
                // (Enter especially) get interpreted as Alt+key by Windows and pop the system menu
                // instead of reaching the focused TextBox.
                var capturedHwnd = _target.CapturedHwnd;
                var targetWindow = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => new System.Windows.Interop.WindowInteropHelper(w).Handle == capturedHwnd);
                if (targetWindow is not null)
                {
                    targetWindow.Activate();
                }
                else
                {
                    // No WPF Window matches the HWND (rare — host-less HWND we still own) — fall
                    // back to the standard restore path. SendAltTap may cause the system-menu
                    // issue but at least focus moves.
                    _target.TryRestoreCaptured();
                }

                var focused = Keyboard.FocusedElement;
                if (focused is null) return false;
                if (!System.Windows.Input.ApplicationCommands.Paste.CanExecute(null, focused)) return false;
                System.Windows.Input.ApplicationCommands.Paste.Execute(null, focused);
                // Refocus to make sure the next keystroke from the user lands HERE — Paste.Execute
                // on a TextBox sometimes leaves logical focus on the element but doesn't restore
                // the keyboard focus the way an interactive Ctrl+V would.
                if (focused is System.Windows.UIElement ue) Keyboard.Focus(ue);
                _logger.LogDebug("AutoPaster: WPF programmatic Paste invoked on {Type}", focused.GetType().Name);
                return true;
            });
            if (done) return;
            _logger.LogDebug("AutoPaster: own-process target couldn't accept WPF Paste — falling back to SendInput.");
        }

        if (restoreForeground)
        {
            // Wait for Enter to be physically released BEFORE swapping the foreground. Enter is
            // the paste trigger when the user reaches us via Enter from the popup — Windows
            // auto-repeat keeps firing WM_KEYDOWN every ~30ms while the key is held. If the
            // foreground swap happens with Enter still down, the next auto-repeat tick lands on
            // the target window; for a TextBox+IsDefault=True dialog (e.g. our own
            // WebpageUrlDialog), that's read as "click the default button" and the dialog
            // dismisses before our Ctrl+V can paste. Cap the wait at 500ms so a stuck Enter
            // doesn't hang the paste forever — after the timeout we proceed anyway.
            var waitStart = Environment.TickCount;
            while ((AppNativeMethods.GetAsyncKeyState(AppNativeMethods.VkReturn) & 0x8000) != 0
                && Environment.TickCount - waitStart < 500)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            // Now safe to swap foreground — Enter is up (or we timed out).
            var restoreOk = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var restored = _target.TryRestoreCaptured();
                _logger.LogDebug("AutoPaster: TryRestoreCaptured returned {Ok}", restored);
                return restored;
            });

            if (!restoreOk)
            {
                _logger.LogWarning("AutoPaster: TryRestoreCaptured returned false; not sending Ctrl+V");
                return;
            }
        }
        // restoreForeground=false: caller's UI is non-activating (KeySequences overlay) — foreground
        // is already on the target where Ctrl+V should land, and the SendAltTap inside
        // TryRestoreCaptured would un-focus apps that use Alt for menu activation.

        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        // If the user reached us via a hotkey like Win+Shift+P, those modifiers may still be
        // physically held when we get here — sending Ctrl+V on top would yield Win+Ctrl+V (audio
        // mixer in Win11), Win+Shift+V, etc., not the plain paste we want. Release them first;
        // a tiny settle delay lets the up events propagate before the V down arrives.
        var released = KeyInjector.ReleaseStickyModifiers();
        if (released > 0)
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);

        var sent = SendCtrlV();
        _logger.LogDebug("AutoPaster: released {Released} sticky modifiers, SendInput returned {Sent} (expected 4)", released, sent);
    }

    private static uint SendCtrlV()
    {
        var inputs = new AppNativeMethods.INPUT[4];
        inputs[0] = MakeKey(AppNativeMethods.VkControl, keyUp: false);
        inputs[1] = MakeKey(AppNativeMethods.VkV, keyUp: false);
        inputs[2] = MakeKey(AppNativeMethods.VkV, keyUp: true);
        inputs[3] = MakeKey(AppNativeMethods.VkControl, keyUp: true);
        return AppNativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<AppNativeMethods.INPUT>());
    }

    private static AppNativeMethods.INPUT MakeKey(ushort virtualKey, bool keyUp) => new()
    {
        type = AppNativeMethods.InputKeyboard,
        u = new AppNativeMethods.InputUnion
        {
            ki = new AppNativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? AppNativeMethods.KeyEventfKeyUp : 0
            }
        }
    };
}
