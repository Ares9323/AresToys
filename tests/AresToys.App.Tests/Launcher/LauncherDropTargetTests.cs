using System.IO;
using AresToys.App.Services;
using AresToys.App.Services.Launcher;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>What a cell ends up holding after something is dropped on it. Covers issue #12
/// (a dropped .lnk must store the shortcut's real target, with its arguments split into the
/// Arguments field) alongside the Start-menu packaged-app case.</summary>
public sealed class LauncherDropTargetTests : IDisposable
{
    private readonly string _dir;

    public LauncherDropTargetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arestoys-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir, best effort */ }
    }

    private string MakeFile(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void DroppedShortcutStoresTheRealTargetNotTheShortcut()
    {
        var target = MakeFile("RealApp.exe");
        var lnk = Path.Combine(_dir, "RealApp.lnk");
        ShellShortcut.Create(target, lnk);

        var result = LauncherDropTarget.Resolve(lnk);

        Assert.Equal(target, result.Path, ignoreCase: true);
        Assert.Equal(string.Empty, result.Arguments);
    }

    [Fact]
    public void DroppedShortcutMovesItsArgumentsIntoTheArgumentsField()
    {
        var target = MakeFile("browser.exe");
        var lnk = Path.Combine(_dir, "Work Profile.lnk");
        ShellShortcut.Create(target, lnk, arguments: "--profile-directory=\"Profile 2\"");

        var result = LauncherDropTarget.Resolve(lnk);

        Assert.Equal(target, result.Path, ignoreCase: true);
        Assert.Equal("--profile-directory=\"Profile 2\"", result.Arguments);
    }

    [Fact]
    public void ShortcutLabelComesFromTheShortcutNameNotTheTargetFilename()
    {
        // The shortcut is what the user recognises: a Start-menu "Google Chrome.lnk" pointing at
        // chrome.exe should label the cell "Google Chrome", not "chrome".
        var target = MakeFile("chrome.exe");
        var lnk = Path.Combine(_dir, "Google Chrome.lnk");
        ShellShortcut.Create(target, lnk);

        Assert.Equal("Google Chrome", LauncherDropTarget.Resolve(lnk).Label);
    }

    [Fact]
    public void ShortcutWithACustomIconKeepsThatIconOnTheCell()
    {
        // Resolving to the target would otherwise throw away the icon the user was looking at.
        var target = MakeFile("launcher.exe");
        var icon = MakeFile("pretty.ico");
        var lnk = Path.Combine(_dir, "Pretty.lnk");
        ShellShortcut.Create(target, lnk, iconPath: icon, iconIndex: 2);

        var result = LauncherDropTarget.Resolve(lnk);

        Assert.Equal(icon, result.IconPath, ignoreCase: true);
        Assert.Equal(2, result.IconIndex);
    }

    [Fact]
    public void ShortcutWhoseIconIsJustItsTargetLeavesTheIconOverrideEmpty()
    {
        // ShellShortcut.Create defaults the icon location to the target itself (Explorer's own
        // default). That's not a custom icon — pinning it would freeze the cell to an icon the
        // shell already resolves for free.
        var target = MakeFile("plain.exe");
        var lnk = Path.Combine(_dir, "Plain.lnk");
        ShellShortcut.Create(target, lnk);

        Assert.Equal(string.Empty, LauncherDropTarget.Resolve(lnk).IconPath);
    }

    [Fact]
    public void ShortcutMarkedRunAsAdministratorCarriesThatOntoTheCell()
    {
        var target = MakeFile("admin-tool.exe");
        var lnk = Path.Combine(_dir, "Admin Tool.lnk");
        ShellShortcut.Create(target, lnk, runAsAdmin: true);

        Assert.True(LauncherDropTarget.Resolve(lnk).RunAsAdmin);
    }

    [Theory]
    [InlineData(ShellShortcut.ShowNormal, LauncherWindowMode.Normal)]
    [InlineData(ShellShortcut.ShowMaximized, LauncherWindowMode.Maximized)]
    [InlineData(ShellShortcut.ShowMinimized, LauncherWindowMode.Minimized)]
    public void ShortcutWindowStateCarriesOntoTheCell(int showCommand, LauncherWindowMode expected)
    {
        var target = MakeFile("windowed.exe");
        var lnk = Path.Combine(_dir, $"Windowed{showCommand}.lnk");
        ShellShortcut.Create(target, lnk, showCommand: showCommand);

        Assert.Equal(expected, LauncherDropTarget.Resolve(lnk).WindowMode);
    }

    [Fact]
    public void PlainFilesGetTheNeutralWindowAndElevationDefaults()
    {
        var result = LauncherDropTarget.Resolve(MakeFile("plain2.exe"));

        Assert.Equal(LauncherWindowMode.Normal, result.WindowMode);
        Assert.False(result.RunAsAdmin);
    }

    [Fact]
    public void ShortcutWithAMissingTargetKeepsPointingAtTheShortcut()
    {
        // MSI-advertised shortcuts and links to uninstalled apps resolve to nothing usable.
        // The .lnk itself still launches (the shell repairs / prompts), so don't degrade it.
        var lnk = Path.Combine(_dir, "Ghost.lnk");
        ShellShortcut.Create(Path.Combine(_dir, "does-not-exist.exe"), lnk);

        Assert.Equal(lnk, LauncherDropTarget.Resolve(lnk).Path, ignoreCase: true);
    }

    [Fact]
    public void ACorruptShortcutKeepsPointingAtTheShortcut()
    {
        var lnk = Path.Combine(_dir, "Broken.lnk");
        File.WriteAllText(lnk, "not a shell link at all");

        var result = LauncherDropTarget.Resolve(lnk);

        Assert.Equal(lnk, result.Path, ignoreCase: true);
        Assert.Equal("Broken", result.Label);
    }

    [Fact]
    public void PlainFilesAreStoredAsIsWithTheFilenameAsLabel()
    {
        var exe = MakeFile("Notepad.exe");

        var result = LauncherDropTarget.Resolve(exe);

        Assert.Equal(exe, result.Path);
        Assert.Equal("Notepad", result.Label);
        Assert.Equal(string.Empty, result.Arguments);
        Assert.Equal(string.Empty, result.IconPath);
    }

    [Fact]
    public void FoldersAreStoredAsIsWithTheFolderNameAsLabel()
    {
        var folder = Path.Combine(_dir, "My Projects");
        Directory.CreateDirectory(folder);

        var result = LauncherDropTarget.Resolve(folder);

        Assert.Equal(folder, result.Path);
        Assert.Equal("My Projects", result.Label);
    }

    [Fact]
    public void PackagedAppIdsAreNormalisedToTheirShellForm()
    {
        const string aumid = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App";

        var result = LauncherDropTarget.Resolve(aumid);

        Assert.Equal(@"shell:AppsFolder\" + aumid, result.Path);
        Assert.False(string.IsNullOrWhiteSpace(result.Label));
        Assert.DoesNotContain("!", result.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlPanelShortcutResolvesToItsShellLocation()
    {
        // Issue #12 follow-up: Control Panel shortcuts carry no filesystem path, only an ID list.
        // The cell gets the shell: form of the page, which ShellExecute opens directly.
        var lnk = Path.Combine(_dir, "Hardware and Sound - Shortcut.lnk");
        ShellShortcut.CreateForShellItem(@"::{26EE0668-A00A-44D7-9371-BEB064C98683}\2", lnk);

        var result = LauncherDropTarget.Resolve(lnk);

        Assert.Equal(@"shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}\2", result.Path, ignoreCase: true);
        Assert.Equal("Hardware and Sound", result.Label);
    }

    [Fact]
    public void ShortcutSuffixIsDroppedFromTheLabel()
    {
        var target = MakeFile("tool.exe");
        var lnk = Path.Combine(_dir, "tool.exe - Shortcut.lnk");
        ShellShortcut.Create(target, lnk);

        Assert.Equal("tool.exe", LauncherDropTarget.Resolve(lnk).Label);
    }

    [Fact]
    public void PlainFilesKeepAShortcutLookingName()
    {
        // Only shortcuts get the suffix stripped: a real file named like that is the user's
        // own choice of name.
        Assert.Equal("Notes - Shortcut", LauncherDropTarget.Resolve(MakeFile("Notes - Shortcut.txt")).Label);
    }

    [Theory]
    [InlineData("Hardware and Sound - Shortcut", "Hardware and Sound")]
    [InlineData("Hardware and Sound - Shortcut (2)", "Hardware and Sound")]
    [InlineData("Hardware e suoni - Collegamento", "Hardware e suoni")]
    [InlineData("Hardware und Sound - Verknüpfung", "Hardware und Sound")]
    [InlineData("notepad.exe - shortcut", "notepad.exe")]
    [InlineData("Shortcut tools", "Shortcut tools")]
    [InlineData("My - Shortcut thing", "My - Shortcut thing")]
    [InlineData(" - Shortcut", " - Shortcut")]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("", "")]
    public void StripShortcutSuffixOnlyTouchesATrailingSuffix(string label, string expected)
    {
        Assert.Equal(expected, LauncherDropTarget.StripShortcutSuffix(label));
    }

    [Theory]
    [InlineData(@"::{26EE0668-A00A-44D7-9371-BEB064C98683}\2", @"shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}\2")]
    [InlineData(@"::{4234D49B-0245-4DF3-B780-3893943456E1}\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"C:\Some\Path.exe", @"C:\Some\Path.exe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShellItemTargetMapsParsingNamesToLaunchableForms(string? parsingName, string expected)
    {
        Assert.Equal(expected, LauncherDropTarget.ShellItemTarget(parsingName));
    }

    [Fact]
    public void EmptyInputProducesAnEmptyTarget()
    {
        var result = LauncherDropTarget.Resolve("");

        Assert.Equal(string.Empty, result.Path);
        Assert.Equal(string.Empty, result.Label);
    }
}
