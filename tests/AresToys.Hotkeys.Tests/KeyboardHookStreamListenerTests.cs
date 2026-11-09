using Xunit;

namespace AresToys.Hotkeys.Tests;

/// <summary>Covers the <see cref="KeyboardHook"/> stream-listener API added for the Key
/// Sequences feature. Stream listeners are pure observers — they receive every non-injected
/// KEYDOWN/KEYUP, can NOT suppress events, and must coexist with the existing atomic-binding
/// suppression path.
///
/// The hook is driven via the <c>internal</c> <see cref="KeyboardHook.InvokeHookForTest"/>
/// seam (exposed to this assembly via <c>InternalsVisibleTo</c>). HookProc itself stays
/// private; the seam marshals the KBDLLHOOKSTRUCT to unmanaged memory and delegates to the
/// real HookProc so tests exercise production code paths rather than a parallel
/// reimplementation. Listener dispatch is asynchronous (ThreadPool); tests wait on a
/// CountdownEvent / ManualResetEventSlim with a generous timeout instead of sleeping.</summary>
public class KeyboardHookStreamListenerTests
{
    // Same timeout the WPF Dispatcher uses for "should have happened by now" — generous enough
    // for slow CI agents, short enough that genuine bugs don't hang the suite for a minute.
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(2);

    private const uint VK_A = 0x41;
    private const uint VK_B = 0x42;

    private static KeyboardHook.KBDLLHOOKSTRUCT MakeData(uint vkCode, uint flags = 0)
        => new() { vkCode = vkCode, scanCode = 0, flags = flags, time = 0, dwExtraInfo = IntPtr.Zero };

    [Fact]
    public void RegisterStreamListener_NullListener_Throws()
    {
        using var hook = new KeyboardHook();
        Assert.Throws<ArgumentNullException>(() => hook.RegisterStreamListener(null!));
    }

    [Fact]
    public void UnregisterStreamListener_Unknown_SilentlyNoOps()
    {
        using var hook = new KeyboardHook();
        // Should not throw and should not affect any state.
        hook.UnregisterStreamListener(_ => { });
        Assert.Equal(0, hook.StreamListenerCount);
    }

    [Fact]
    public void RegisterStreamListener_ReceivesKeyDownAndKeyUp()
    {
        using var hook = new KeyboardHook();
        var down = new ManualResetEventSlim(false);
        var up = new ManualResetEventSlim(false);
        KeyEvent? downEvent = null;
        KeyEvent? upEvent = null;

        hook.RegisterStreamListener(e =>
        {
            if (e.IsDown) { downEvent = e; down.Set(); }
            else { upEvent = e; up.Set(); }
        });

        hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYDOWN);
        hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYUP);

        Assert.True(down.Wait(DispatchTimeout), "KEYDOWN was not delivered to listener");
        Assert.True(up.Wait(DispatchTimeout), "KEYUP was not delivered to listener");
        Assert.NotNull(downEvent);
        Assert.NotNull(upEvent);
        Assert.Equal(VK_A, downEvent!.VkCode);
        Assert.True(downEvent.IsDown);
        Assert.Equal(VK_A, upEvent!.VkCode);
        Assert.False(upEvent.IsDown);
    }

    [Fact]
    public void RegisterStreamListener_MultipleListeners_AllReceiveEvent()
    {
        using var hook = new KeyboardHook();
        var l1 = new ManualResetEventSlim(false);
        var l2 = new ManualResetEventSlim(false);
        var l3 = new ManualResetEventSlim(false);

        hook.RegisterStreamListener(_ => l1.Set());
        hook.RegisterStreamListener(_ => l2.Set());
        hook.RegisterStreamListener(_ => l3.Set());

        hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYDOWN);

        Assert.True(l1.Wait(DispatchTimeout));
        Assert.True(l2.Wait(DispatchTimeout));
        Assert.True(l3.Wait(DispatchTimeout));
    }

    [Fact]
    public void UnregisterStreamListener_StopsDeliveryToThatListenerOnly()
    {
        using var hook = new KeyboardHook();
        var l1Count = 0;
        var l2Count = 0;
        var l2Signal = new ManualResetEventSlim(false);

        void L1(KeyEvent _) => Interlocked.Increment(ref l1Count);
        void L2(KeyEvent _) { Interlocked.Increment(ref l2Count); l2Signal.Set(); }

        hook.RegisterStreamListener(L1);
        hook.RegisterStreamListener(L2);

        hook.UnregisterStreamListener(L1);
        Assert.Equal(1, hook.StreamListenerCount);

        hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYDOWN);

        Assert.True(l2Signal.Wait(DispatchTimeout), "L2 should still receive events");
        // Give any spurious L1 invocation a chance to settle before asserting it didn't run.
        // We can't avoid this short wait without coupling to dispatcher internals — but we
        // can keep it tight (50 ms is well above the ThreadPool scheduling latency).
        Thread.Sleep(50);
        Assert.Equal(0, l1Count);
        Assert.Equal(1, l2Count);
    }

    [Fact]
    public void RegisterStreamListener_ThrowingListener_DoesNotCrashHookOrBlockOthers()
    {
        using var hook = new KeyboardHook();
        var goodCalled = new ManualResetEventSlim(false);

        hook.RegisterStreamListener(_ => throw new InvalidOperationException("boom"));
        hook.RegisterStreamListener(_ => goodCalled.Set());

        // Should not propagate — the hook swallows listener exceptions like it does
        // atomic-binding callbacks.
        var ex = Record.Exception(() => hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYDOWN));
        Assert.Null(ex);

        Assert.True(goodCalled.Wait(DispatchTimeout),
            "well-behaved listener must still receive the event even when a sibling throws");
    }

    [Fact]
    public void StreamListener_FiresEvenWhenAtomicBindingMatchesAndSuppresses()
    {
        using var hook = new KeyboardHook();
        var listenerSaw = new ManualResetEventSlim(false);
        var bindingFired = new ManualResetEventSlim(false);

        hook.RegisterStreamListener(_ => listenerSaw.Set());
        // No modifiers → the atomic match needs HotkeyModifiers.None.
        hook.Register("test-bind", HotkeyModifiers.None, VK_A, () => bindingFired.Set(), suppress: true);

        var suppressed = hook.InvokeHookForTest(MakeData(VK_A), (IntPtr)KeyboardHook.WM_KEYDOWN);

        // The atomic binding must still suppress (proves we didn't break existing behavior).
        Assert.Equal(1, suppressed);
        Assert.True(bindingFired.Wait(DispatchTimeout), "atomic binding callback must fire");
        // Stream listener must also have seen the event — observers don't get gated by suppression.
        Assert.True(listenerSaw.Wait(DispatchTimeout),
            "stream listener must observe events even when an atomic binding suppresses them");
    }

    [Fact]
    public void StreamListener_DoesNotFireForInjectedEvents()
    {
        using var hook = new KeyboardHook();
        var listenerCount = 0;
        hook.RegisterStreamListener(_ => Interlocked.Increment(ref listenerCount));

        // LLKHF_INJECTED flag set → hook short-circuits before any listener dispatch.
        hook.InvokeHookForTest(MakeData(VK_A, flags: KeyboardHook.LLKHF_INJECTED), (IntPtr)KeyboardHook.WM_KEYDOWN);
        hook.InvokeHookForTest(MakeData(VK_A, flags: KeyboardHook.LLKHF_INJECTED), (IntPtr)KeyboardHook.WM_KEYUP);

        // Allow any (incorrectly-dispatched) work to settle before asserting.
        Thread.Sleep(100);
        Assert.Equal(0, listenerCount);
    }

    [Fact]
    public void Dispose_ClearsStreamListeners()
    {
        var hook = new KeyboardHook();
        hook.RegisterStreamListener(_ => { });
        hook.RegisterStreamListener(_ => { });
        Assert.Equal(2, hook.StreamListenerCount);

        hook.Dispose();

        Assert.Equal(0, hook.StreamListenerCount);
    }

    [Fact]
    public void StreamListener_ReceivesEventForUnmatchedKey()
    {
        // Sanity: when there's NO atomic binding at all, the stream listener still fires
        // (this is the main use case — Key Sequences tracker observing arbitrary typing).
        using var hook = new KeyboardHook();
        var signal = new ManualResetEventSlim(false);
        KeyEvent? captured = null;

        hook.RegisterStreamListener(e => { captured = e; signal.Set(); });

        hook.InvokeHookForTest(MakeData(VK_B), (IntPtr)KeyboardHook.WM_KEYDOWN);

        Assert.True(signal.Wait(DispatchTimeout));
        Assert.NotNull(captured);
        Assert.Equal(VK_B, captured!.VkCode);
        Assert.True(captured.IsDown);
    }
}
