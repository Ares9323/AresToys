using Xunit;

namespace AresToys.Hotkeys.Tests;

/// <summary>A workflow that repeats while its hotkey is held (Repeat "while held") reads <see cref="KeyboardHook.IsHeld"/>.
/// Its Press key step releases the held Ctrl before it injects, so the auto repeats of the trigger key stop matching
/// the binding. They must still be swallowed and keep the key held, and the release must end the hold.</summary>
public class KeyboardHookHeldRepeatTests
{
    private const uint VK_UP = 0x26;

    private static KeyboardHook.KBDLLHOOKSTRUCT At(uint vk, int tick)
        => new() { vkCode = vk, time = unchecked((uint)tick), dwExtraInfo = IntPtr.Zero };

    [Fact]
    public void RepeatsAfterTheBindingStopsMatching_AreSwallowedAndKeepTheKeyHeld()
    {
        using var hook = new KeyboardHook();
        hook.Register("wf", HotkeyModifiers.None, VK_UP, () => { }, suppress: true);
        // Event times in the recent past: IsHeld measures the last KEYDOWN against the current tick.
        var tick = Environment.TickCount - 200;

        Assert.Equal(1, hook.InvokeHookForTest(At(VK_UP, tick), (IntPtr)KeyboardHook.WM_KEYDOWN));
        // Stand-in for "the modifiers no longer match": the binding is gone, only the swallowed press remains.
        hook.Unregister("wf");

        Assert.Equal(1, hook.InvokeHookForTest(At(VK_UP, tick + 30), (IntPtr)KeyboardHook.WM_KEYDOWN));
        Assert.True(hook.IsHeld(VK_UP));

        Assert.Equal(1, hook.InvokeHookForTest(At(VK_UP, tick + 60), (IntPtr)KeyboardHook.WM_KEYUP));
        Assert.False(hook.IsHeld(VK_UP));

        // The next press of the unbound key goes through to the app.
        Assert.Equal(0, hook.InvokeHookForTest(At(VK_UP, tick + 2_000), (IntPtr)KeyboardHook.WM_KEYDOWN));
    }

    [Fact]
    public void CtrlStillHeldAfterAnInjectedRelease_StillMatchesTheNextPress()
    {
        const uint VK_LCONTROL = 0xA2;
        using var hook = new KeyboardHook();
        var fired = 0;
        hook.Register("wf", HotkeyModifiers.Control, VK_UP, () => Interlocked.Increment(ref fired), suppress: true);
        var tick = Environment.TickCount - 200;

        // The user holds Ctrl; a Press key step injects its release, so Windows now sees Ctrl up.
        hook.InvokeHookForTest(At(VK_LCONTROL, tick), (IntPtr)KeyboardHook.WM_KEYDOWN);
        hook.InvokeHookForTest(new KeyboardHook.KBDLLHOOKSTRUCT { vkCode = VK_LCONTROL, time = unchecked((uint)(tick + 10)), flags = KeyboardHook.LLKHF_INJECTED }, (IntPtr)KeyboardHook.WM_KEYUP);

        // A fresh Up press with Ctrl still physically down matches Ctrl+Up.
        Assert.Equal(1, hook.InvokeHookForTest(At(VK_UP, tick + 20), (IntPtr)KeyboardHook.WM_KEYDOWN));
        Assert.Equal(1, hook.InvokeHookForTest(At(VK_UP, tick + 40), (IntPtr)KeyboardHook.WM_KEYUP));

        // The user's own Ctrl release ends it: plain Up goes through.
        hook.InvokeHookForTest(At(VK_LCONTROL, tick + 60), (IntPtr)KeyboardHook.WM_KEYUP);
        Assert.Equal(0, hook.InvokeHookForTest(At(VK_UP, tick + 80), (IntPtr)KeyboardHook.WM_KEYDOWN));
    }
}
