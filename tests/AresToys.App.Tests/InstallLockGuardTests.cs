using System.Collections.Generic;
using System.Linq;
using AresToys.Updater;
using Xunit;

namespace AresToys.App.Tests;

public class InstallLockGuardTests
{
    [Fact]
    public void FilterLockers_KeepsExternalProcess()
    {
        var candidates = new[]
        {
            new LockerCandidate(4321, "acrotray", "Adobe Acrobat", false),
        };
        var result = InstallLockGuard.FilterLockers(candidates, currentPid: 1000);
        var only = Assert.Single(result);
        Assert.Equal(4321, only.Pid);
        Assert.Equal("acrotray", only.ProcessName);
        Assert.Equal("Adobe Acrobat", only.AppName);
    }

    [Fact]
    public void FilterLockers_ExcludesSelfOtherAresToysAndCritical()
    {
        var candidates = new[]
        {
            new LockerCandidate(1000, "AresToys", "AresToys", false),   // current process
            new LockerCandidate(2000, "AresToys", "AresToys", false),   // another instance
            new LockerCandidate(3000, "csrss", "Windows", true),         // critical system
            new LockerCandidate(4321, "acrotray", "Adobe Acrobat", false), // keep
        };
        var result = InstallLockGuard.FilterLockers(candidates, currentPid: 1000);
        Assert.Equal(new[] { 4321 }, result.Select(p => p.Pid).ToArray());
    }
}
