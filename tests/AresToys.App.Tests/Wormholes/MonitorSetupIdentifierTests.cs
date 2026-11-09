using AresToys.App.Services.Wormholes;
using AresToys.Capture;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>
/// Stability + invariants of <see cref="MonitorSetupIdentifier"/>. The hash drives which
/// per-setup positions file is loaded, so a stable digest across re-enumerations matters:
/// flaky hashes would create a new "setup" on every display event and never reuse the
/// user's carefully-placed layout.
/// </summary>
public class MonitorSetupIdentifierTests
{
    [Fact]
    public void EmptyMonitorList_ReturnsSentinel()
    {
        Assert.Equal("no-monitors", MonitorSetupIdentifier.ComputeSetupHash(Array.Empty<MonitorInfo>()));
    }

    [Fact]
    public void SameMonitors_DifferentEnumerationOrder_ProduceSameHash()
    {
        // The store relies on this — if EnumDisplayMonitors happens to walk displays
        // in a different order between sessions, we still want the same setup file.
        var ordered = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0,    Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
            new MonitorInfo("\\\\.\\DISPLAY2", X: 1920, Y: 0, Width: 2560, Height: 1440, IsPrimary: false),
        };
        var shuffled = new[] { ordered[1], ordered[0] };
        Assert.Equal(
            MonitorSetupIdentifier.ComputeSetupHash(ordered),
            MonitorSetupIdentifier.ComputeSetupHash(shuffled));
    }

    [Fact]
    public void DifferentResolution_ProducesDifferentHash()
    {
        // RDP single-screen at 1366×768 must hash distinctly from the home 1920×1080
        // primary — that's what makes the per-setup file split actually do its job.
        var home = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0, Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
        };
        var rdp = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0, Y: 0, Width: 1366, Height: 768, IsPrimary: true),
        };
        Assert.NotEqual(
            MonitorSetupIdentifier.ComputeSetupHash(home),
            MonitorSetupIdentifier.ComputeSetupHash(rdp));
    }

    [Fact]
    public void DifferentDeviceName_ProducesDifferentHash()
    {
        // Same resolution on a physically-different monitor (e.g. \DISPLAY2 vs \DISPLAY1)
        // is still a distinct setup — the user might want different positions on each.
        var a = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0, Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
        };
        var b = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY2", X: 0, Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
        };
        Assert.NotEqual(
            MonitorSetupIdentifier.ComputeSetupHash(a),
            MonitorSetupIdentifier.ComputeSetupHash(b));
    }

    [Fact]
    public void DifferentVirtualScreenPosition_SameMonitors_ProducesSameHash()
    {
        // Rearranging monitors in Display Settings (the user swaps which one is left vs
        // right) shouldn't fragment their setup file — only resolution + device name should
        // matter. Catches over-partitioning.
        var leftRight = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0,    Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
            new MonitorInfo("\\\\.\\DISPLAY2", X: 1920, Y: 0, Width: 1920, Height: 1080, IsPrimary: false),
        };
        var rightLeft = new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 1920, Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
            new MonitorInfo("\\\\.\\DISPLAY2", X: 0,    Y: 0, Width: 1920, Height: 1080, IsPrimary: false),
        };
        Assert.Equal(
            MonitorSetupIdentifier.ComputeSetupHash(leftRight),
            MonitorSetupIdentifier.ComputeSetupHash(rightLeft));
    }

    [Fact]
    public void HashLength_Is16HexChars()
    {
        // The store uses the hash verbatim as a filename — pin the length so the on-disk
        // names stay readable + filesystem-safe forever.
        var hash = MonitorSetupIdentifier.ComputeSetupHash(new[]
        {
            new MonitorInfo("\\\\.\\DISPLAY1", X: 0, Y: 0, Width: 1920, Height: 1080, IsPrimary: true),
        });
        Assert.Equal(16, hash.Length);
        Assert.All(hash, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"non-hex character '{c}' in hash {hash}"));
    }
}
