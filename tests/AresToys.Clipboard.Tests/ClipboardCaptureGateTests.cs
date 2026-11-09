using Microsoft.Extensions.Options;
using AresToys.Clipboard.Tests.Fakes;
using Xunit;

namespace AresToys.Clipboard.Tests;

public class ClipboardCaptureGateTests
{
    private static (ClipboardCaptureGate Gate, FakeForegroundProcessProbe Probe, CaptureGateOptions Options) Create()
    {
        var options = new CaptureGateOptions();
        var monitor = new TestOptionsMonitor(options);
        var probe = new FakeForegroundProcessProbe();
        return (new ClipboardCaptureGate(probe, monitor), probe, options);
    }

    [Fact]
    public void Evaluate_AllowsByDefault_WhenForegroundIsHarmless()
    {
        var (gate, probe, _) = Create();
        probe.ProcessName = "notepad.exe";

        var decision = gate.Evaluate();

        Assert.True(decision.Allow);
    }

    [Fact]
    public void Evaluate_BlocksDefaultPasswordManagers()
    {
        var (gate, probe, _) = Create();

        probe.ProcessName = "KeePassXC.exe";
        Assert.False(gate.Evaluate().Allow);

        probe.ProcessName = "1Password";
        Assert.False(gate.Evaluate().Allow);
    }

    [Fact]
    public void Evaluate_BlocksAreCaseInsensitive()
    {
        var (gate, probe, _) = Create();
        probe.ProcessName = "keepass.EXE";

        Assert.False(gate.Evaluate().Allow);
    }

    [Fact]
    public void Evaluate_AllowsWhenProcessIsUnknown()
    {
        var (gate, probe, _) = Create();
        probe.ProcessName = null;

        Assert.True(gate.Evaluate().Allow);
    }

    [Fact]
    public void Evaluate_BlocksWhenIncognitoActive_RegardlessOfProcess()
    {
        var (gate, probe, opts) = Create();
        probe.ProcessName = "notepad.exe";
        opts.IncognitoActive = true;

        var decision = gate.Evaluate();

        Assert.False(decision.Allow);
        Assert.Equal("incognito", decision.Reason);
    }

    [Fact]
    public void Evaluate_AdditionsToBlocklist_TakeEffectImmediately()
    {
        var (gate, probe, opts) = Create();
        probe.ProcessName = "MyVault.exe";
        Assert.True(gate.Evaluate().Allow);

        opts.BlockedProcesses.Add("MyVault.exe");

        Assert.False(gate.Evaluate().Allow);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<CaptureGateOptions>
    {
        public TestOptionsMonitor(CaptureGateOptions value) { CurrentValue = value; }
        public CaptureGateOptions CurrentValue { get; }
        public CaptureGateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CaptureGateOptions, string?> listener) => null;
    }
}
