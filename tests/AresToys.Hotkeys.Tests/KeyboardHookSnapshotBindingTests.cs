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
}
