using AresToys.App.Services.Zones;
using Xunit;

namespace AresToys.App.Tests;

public sealed class ZoneTitleMatchTests
{
    [Theory]
    [InlineData("✳ FilamentManager", "filamentmanager")]
    [InlineData("  ⠂ Chromimic ", "chromimic")]
    [InlineData("cmd", "cmd")]
    public void NormalizeTitle_DropsLeadingSymbols(string title, string expected)
        => Assert.Equal(expected, ZoneWindowTracker.NormalizeTitle(title));

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(null, false)]
    public void IsTerminal_OnlyForTerminalHosts(string? path, bool expected)
        => Assert.Equal(expected, ZoneWindowTracker.IsTerminal(path));

    [Theory]
    [InlineData("build", "build", true)]
    [InlineData("build", "build server - terminal", true)]
    [InlineData("build server - terminal", "build", true)]
    [InlineData("build", "logs", false)]
    [InlineData("", "logs", false)]
    [InlineData("build", "", false)]
    public void TitlesMatch_IsEqualOrContains(string remembered, string current, bool expected)
        => Assert.Equal(expected, ZoneWindowTracker.TitlesMatch(remembered, current));
}
