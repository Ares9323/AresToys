using Xunit;

namespace AresToys.Hotkeys.Tests;

/// <summary>Regression cover for keyup-only trigger keys (PrintScreen / Pause). Windows consumes
/// their WM_KEYDOWN before any low-level hook sees it, so the hook matches them on the KEYUP
/// edge instead. A bug in the <c>_suppressedKeyUps</c> bookkeeping (added for the VK_APPS
/// on-release menu-pop fix) registered the vk on EVERY suppressed match — including these
/// keyup matches — which made the NEXT press's keyup get mistaken for a "paired release" and
/// swallowed, so PrintScreen fired only on every OTHER press. These tests pin the fix: the
/// callback must fire on every press, and suppression must still happen.</summary>
public class KeyboardHookSnapshotBindingTests
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(2);

    private const uint VK_SNAPSHOT = 0x2C; // PrintScreen
    private const uint VK_PAUSE = 0x13;    // Pause / Break

    private static KeyboardHook.KBDLLHOOKSTRUCT MakeData(uint vkCode)
        => new() { vkCode = vkCode, scanCode = 0, flags = 0, time = 0, dwExtraInfo = IntPtr.Zero };

    [Theory]
    [InlineData(VK_SNAPSHOT)]
    [InlineData(VK_PAUSE)]
    public void KeyupOnlyKey_FiresCallbackOnEveryPress_NotEveryOther(uint vk)
    {
        using var hook = new KeyboardHook();
        var fires = 0;
        var third = new CountdownEvent(3);
        hook.Register("snap", HotkeyModifiers.None, vk, () =>
        {
            Interlocked.Increment(ref fires);
            third.Signal();
        }, suppress: true);

        // These keys deliver ONLY a KEYUP. Three genuine presses → three KEYUP events.
        for (var i = 0; i < 3; i++)
        {
            var suppressed = hook.InvokeHookForTest(MakeData(vk), (IntPtr)KeyboardHook.WM_KEYUP);
            // Every press must be suppressed (consumed) so the foreground app doesn't also act on it.
            Assert.Equal(1, suppressed);
        }

        Assert.True(third.Wait(DispatchTimeout),
            $"callback should have fired on all 3 presses, fired {fires}");
        Assert.Equal(3, fires);
    }

    /// <summary>Regression for the "Print sometimes ignored" report: Win11 alternates between
    /// "consume KEYDOWN" (only KEYUP reaches the hook) and "deliver both" depending on the
    /// snipping shortcut state and which background process has focus. Mixed delivery used to
    /// leave the vk stale in <c>_heldKeys</c> after a KEYUP-match, then the next press's
    /// KEYDOWN was wrongly debounced as a "repeat" and silently swallowed. Cover the canonical
    /// flip: press 1 = KEYUP only, press 2 = KEYDOWN + KEYUP, press 3 = KEYUP only. All three
    /// must fire.</summary>
    [Fact]
    public void KeyupOnlyKey_MixedWithKeydownDelivery_AllPressesFire()
    {
        using var hook = new KeyboardHook();
        var fires = 0;
        var third = new CountdownEvent(3);
        hook.Register("snap", HotkeyModifiers.None, VK_SNAPSHOT, () =>
        {
            Interlocked.Increment(ref fires);
            third.Signal();
        }, suppress: true);

        // Press 1 — KEYUP only (Windows consumed the KEYDOWN this time).
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(VK_SNAPSHOT), (IntPtr)KeyboardHook.WM_KEYUP));

        // Press 2 — both KEYDOWN and KEYUP arrive. KEYDOWN must fire (it's the leading edge);
        // KEYUP must just be consumed without a second fire.
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(VK_SNAPSHOT), (IntPtr)KeyboardHook.WM_KEYDOWN));
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(VK_SNAPSHOT), (IntPtr)KeyboardHook.WM_KEYUP));

        // Press 3 — back to KEYUP only.
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(VK_SNAPSHOT), (IntPtr)KeyboardHook.WM_KEYUP));

        Assert.True(third.Wait(DispatchTimeout),
            $"all 3 presses must fire across mixed delivery, fired {fires}");
        Assert.Equal(3, fires);
    }

    /// <summary>The freeze behind "second Shift+PrintScreen does nothing while recording": on the
    /// first press Windows delivered the KEYDOWN (which started the recording) but the paired
    /// KEYUP never reached the hook — the capture overlay opening changed the foreground window
    /// and ate it. The old code added the vk to <c>_heldKeys</c> on that KEYDOWN and, with no
    /// KEYUP to clear it, debounced the NEXT KEYDOWN as an auto-repeat and swallowed it. Both
    /// recording hotkeys are bound to PrintScreen (Shift+Print for mp4, Ctrl+Shift+Print for gif),
    /// so BOTH "stop" presses froze while the overlay's own Stop button still worked. Pin the fix:
    /// a KEYDOWN whose KEYUP is dropped must never block the following press.</summary>
    [Theory]
    [InlineData(VK_SNAPSHOT)]
    [InlineData(VK_PAUSE)]
    public void KeyupTriggerKey_KeydownWithDroppedKeyup_NextPressStillFires(uint vk)
    {
        using var hook = new KeyboardHook();
        var fires = 0;
        var second = new CountdownEvent(2);
        hook.Register("snap", HotkeyModifiers.None, vk, () =>
        {
            Interlocked.Increment(ref fires);
            second.Signal();
        }, suppress: true);

        // Press 1 — KEYDOWN arrives and fires (starts the recording). Its KEYUP is DROPPED
        // (overlay-open foreground race), so the hook never sees it.
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(vk), (IntPtr)KeyboardHook.WM_KEYDOWN));

        // Press 2 — user presses again to stop. Must fire despite press 1's lost KEYUP.
        Assert.Equal(1, hook.InvokeHookForTest(MakeData(vk), (IntPtr)KeyboardHook.WM_KEYDOWN));

        Assert.True(second.Wait(DispatchTimeout),
            $"a press after a dropped keyup must still fire, fired {fires}");
        Assert.Equal(2, fires);
    }
}
