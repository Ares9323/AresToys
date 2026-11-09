using Xunit;

namespace AresToys.Hotkeys.Tests;

public class KeyboardHookMouseBindingTests
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const uint XButton1 = 1u << 16;
    private const uint XButton2 = 2u << 16;

    [Fact]
    public void SideButtonBinding_SwallowsPressAndRelease()
    {
        using var hook = new KeyboardHook();
        var fired = new ManualResetEventSlim();
        hook.Register("x1", HotkeyModifiers.None, 0x05, fired.Set);

        Assert.Equal(1, hook.InvokeMouseHookForTest(WM_XBUTTONDOWN, XButton1));
        Assert.True(fired.Wait(2000));
        Assert.Equal(1, hook.InvokeMouseHookForTest(WM_XBUTTONUP, XButton1));
        // The release was paired with the press, so a second stray release goes through.
        Assert.Equal(0, hook.InvokeMouseHookForTest(WM_XBUTTONUP, XButton1));
    }

    [Fact]
    public void BoundButton_IsHeldFromPressToRelease()
    {
        using var hook = new KeyboardHook();
        hook.Register("x1", HotkeyModifiers.None, 0x05, () => { });

        Assert.False(hook.IsHeld(0x05));
        hook.InvokeMouseHookForTest(WM_XBUTTONDOWN, XButton1);
        Assert.True(hook.IsHeld(0x05));
        hook.InvokeMouseHookForTest(WM_XBUTTONUP, XButton1);
        Assert.False(hook.IsHeld(0x05));
    }

    [Fact]
    public void OtherButtons_PassThrough()
    {
        using var hook = new KeyboardHook();
        hook.Register("x1", HotkeyModifiers.None, 0x05, () => { });

        Assert.Equal(0, hook.InvokeMouseHookForTest(WM_XBUTTONDOWN, XButton2));
        Assert.Equal(0, hook.InvokeMouseHookForTest(WM_MBUTTONDOWN, 0));
        Assert.Equal(0, hook.InvokeMouseHookForTest(WM_MBUTTONUP, 0));
    }

    [Fact]
    public void RightClickBoundOnPurpose_IsTaken()
    {
        using var hook = new KeyboardHook();
        hook.Register("right", HotkeyModifiers.None, 0x02, () => { });

        Assert.Equal(1, hook.InvokeMouseHookForTest(0x0204, 0));
        Assert.Equal(1, hook.InvokeMouseHookForTest(0x0205, 0));
    }

    [Fact]
    public void LeftClickWithoutModifier_IsNeverTakenOver()
    {
        using var hook = new KeyboardHook();
        hook.Register("left", HotkeyModifiers.None, 0x01, () => { });

        Assert.Equal(0, hook.InvokeMouseHookForTest(WM_LBUTTONDOWN, 0));
    }
}
