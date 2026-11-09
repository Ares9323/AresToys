using Xunit;

namespace AresToys.Hotkeys.Tests;

public class HotkeyManagerDispatchTests
{
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<(IntPtr Hwnd, int Id, HotkeyModifiers Mod, uint Key)> Registered { get; } = [];
        public bool NextRegisterReturns { get; set; } = true;
        public bool RegisterHotKey(IntPtr hwnd, int id, HotkeyModifiers modifiers, uint virtualKey)
        {
            if (!NextRegisterReturns) return false;
            Registered.Add((hwnd, id, modifiers, virtualKey));
            return true;
        }
        public bool UnregisterHotKey(IntPtr hwnd, int id)
        {
            return Registered.RemoveAll(r => r.Hwnd == hwnd && r.Id == id) > 0;
        }
    }

    private static HotkeyManager CreateAttached(out FakeRegistrar registrar, IntPtr hwnd = default)
    {
        registrar = new FakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.Attach(hwnd == default ? new IntPtr(1234) : hwnd);
        return manager;
    }

    [Fact]
    public void Register_BeforeAttach_Throws()
    {
        var manager = new HotkeyManager(new FakeRegistrar());
        Assert.Throws<InvalidOperationException>(() =>
            manager.Register(new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56)));
    }

    [Fact]
    public void Register_PassesDefinitionToRegistrar()
    {
        using var manager = CreateAttached(out var reg);

        manager.Register(new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56));

        Assert.Single(reg.Registered);
        Assert.Equal(HotkeyModifiers.Win, reg.Registered[0].Mod);
        Assert.Equal(0x56u, reg.Registered[0].Key);
    }

    [Fact]
    public void Register_DuplicateId_ReturnsFalse()
    {
        using var manager = CreateAttached(out var _);
        var def = new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56);

        Assert.True(manager.Register(def));
        Assert.False(manager.Register(def));
    }

    [Fact]
    public void Register_RegistrarRefuses_ReturnsFalse()
    {
        using var manager = CreateAttached(out var reg);
        reg.NextRegisterReturns = false;

        var ok = manager.Register(new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56));

        Assert.False(ok);
    }

    [Fact]
    public void Dispatch_RaisesTriggeredEventForRegisteredId()
    {
        using var manager = CreateAttached(out var reg);
        manager.Register(new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56));

        HotkeyDefinition? raised = null;
        manager.Triggered += (_, args) => raised = args.Definition;
        var assignedWmId = reg.Registered[0].Id;

        var handled = manager.Dispatch(assignedWmId);

        Assert.True(handled);
        Assert.NotNull(raised);
        Assert.Equal("popup", raised!.Id);
    }

    [Fact]
    public void Dispatch_UnknownId_ReturnsFalseAndRaisesNothing()
    {
        using var manager = CreateAttached(out var _);
        var raised = false;
        manager.Triggered += (_, _) => raised = true;

        var handled = manager.Dispatch(0xABCD);

        Assert.False(handled);
        Assert.False(raised);
    }

    [Fact]
    public void Unregister_ExistingId_ReturnsTrueAndRemoves()
    {
        using var manager = CreateAttached(out var reg);
        manager.Register(new HotkeyDefinition("popup", HotkeyModifiers.Win, 0x56));

        var removed = manager.Unregister("popup");

        Assert.True(removed);
        Assert.Empty(reg.Registered);
    }
}
