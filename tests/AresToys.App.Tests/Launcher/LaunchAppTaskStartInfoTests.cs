using System.Diagnostics;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.PipelineTasks;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>The translation from a "Launch app" step's config into what actually gets started.
/// Covers the window-mode and run-as-administrator options added alongside the shortcut
/// unwrapping, plus the packaged-app path normalisation the launcher already does.</summary>
public sealed class LaunchAppTaskStartInfoTests
{
    [Fact]
    public void DefaultsToANormalUnelevatedWindow()
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", null, false);

        Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);
        Assert.Equal(string.Empty, psi.Verb);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void MinimizeAfterStartupStartsTheAppInANormalWindow()
    {
        // The minimising happens afterwards, through WindowMinimizer. Asking the shell for a
        // minimised window here would reintroduce the very behaviour this mode exists to avoid:
        // apps that answer the hint by showing no window at all.
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", "MinimizeAfterStartup", false);

        Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);
        Assert.True(LaunchAppTask.IsMinimizeAfterStartup("MinimizeAfterStartup"));
    }

    [Theory]
    [InlineData("Minimized")]
    [InlineData("Normal")]
    [InlineData("Hidden")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void NoOtherWindowModeAsksForTheAfterTheFactMinimise(string? mode)
    {
        Assert.False(LaunchAppTask.IsMinimizeAfterStartup(mode));
    }

    [Theory]
    [InlineData("Maximized", ProcessWindowStyle.Maximized)]
    [InlineData("Minimized", ProcessWindowStyle.Minimized)]
    [InlineData("Hidden", ProcessWindowStyle.Hidden)]
    [InlineData("Normal", ProcessWindowStyle.Normal)]
    // Case-insensitive so a hand-edited config or an older profile still parses.
    [InlineData("maximized", ProcessWindowStyle.Maximized)]
    [InlineData("HIDDEN", ProcessWindowStyle.Hidden)]
    // Unparseable / absent → neutral default rather than a throw.
    [InlineData("nonsense", ProcessWindowStyle.Normal)]
    [InlineData("", ProcessWindowStyle.Normal)]
    [InlineData(null, ProcessWindowStyle.Normal)]
    public void WindowModeMapsOntoTheProcessWindowStyle(string? mode, ProcessWindowStyle expected)
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", mode, false);

        Assert.Equal(expected, psi.WindowStyle);
    }

    [Fact]
    public void RunAsAdminAsksTheShellForTheElevationPrompt()
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", null, true);

        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);   // runas only works through ShellExecute
    }

    [Fact]
    public void WorkingDirectoryDefaultsToTheTargetsFolder()
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", null, false);

        Assert.Equal(@"C:\Windows", psi.WorkingDirectory);
    }

    [Fact]
    public void AnExplicitWorkingDirectoryWins()
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", @"C:\Temp", null, false);

        Assert.Equal(@"C:\Temp", psi.WorkingDirectory);
    }

    [Fact]
    public void PackagedAppIdsAreNormalisedAndGetNoWorkingDirectory()
    {
        const string aumid = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App";

        var psi = LaunchAppTask.BuildStartInfo(aumid, "", "", null, false);

        Assert.Equal(PackagedAppPath.AppsFolderPrefix + aumid, psi.FileName);
        // "shell:AppsFolder" is not a directory; handing it over as one is meaningless.
        Assert.Equal(string.Empty, psi.WorkingDirectory);
    }

    [Fact]
    public void ArgumentsArePassedThrough()
    {
        var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "--flag \"a b\"", "", null, false);

        Assert.Equal("--flag \"a b\"", psi.Arguments);
    }
}
