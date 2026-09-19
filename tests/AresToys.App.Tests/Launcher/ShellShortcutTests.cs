using System.IO;
using AresToys.App.Services;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Round-trips real <c>.lnk</c> files through the shell: every case writes a shortcut
/// with <see cref="ShellShortcut.Create"/> and reads it back with <see cref="ShellShortcut.TryRead"/>.
/// No mocking — the whole point is that the COM marshalling of IShellLinkW's vtable is right,
/// and only the actual shell can tell us that.</summary>
public sealed class ShellShortcutTests : IDisposable
{
    private readonly string _dir;

    public ShellShortcutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arestoys-lnk-" + Guid.NewGuid().ToString("N"));
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

    private string Lnk(string name) => Path.Combine(_dir, name);

    [Fact]
    public void ReadsBackTheTargetOfAShortcut()
    {
        var target = MakeFile("target.exe");
        var lnk = Lnk("plain.lnk");
        ShellShortcut.Create(target, lnk);

        var info = ShellShortcut.TryRead(lnk);

        Assert.NotNull(info);
        Assert.Equal(target, info!.TargetPath, ignoreCase: true);
        Assert.Equal(string.Empty, info.Arguments);
    }

    [Fact]
    public void ReadsBackTheArgumentsOfAShortcut()
    {
        var target = MakeFile("app.exe");
        var lnk = Lnk("withargs.lnk");
        ShellShortcut.Create(target, lnk, arguments: "--profile \"Work Stuff\" --new-window");

        var info = ShellShortcut.TryRead(lnk);

        Assert.NotNull(info);
        Assert.Equal(target, info!.TargetPath, ignoreCase: true);
        Assert.Equal("--profile \"Work Stuff\" --new-window", info.Arguments);
    }

    [Fact]
    public void ReadsBackACustomIconLocation()
    {
        var target = MakeFile("app.exe");
        var icon = MakeFile("custom.ico");
        var lnk = Lnk("withicon.lnk");
        ShellShortcut.Create(target, lnk, iconPath: icon, iconIndex: 3);

        var info = ShellShortcut.TryRead(lnk);

        Assert.NotNull(info);
        Assert.Equal(icon, info!.IconPath, ignoreCase: true);
        Assert.Equal(3, info.IconIndex);
    }

    [Fact]
    public void ReadsBackTheRunAsAdministratorFlag()
    {
        var target = MakeFile("elevated.exe");
        var lnk = Lnk("elevated.lnk");
        ShellShortcut.Create(target, lnk, runAsAdmin: true);

        var info = ShellShortcut.TryRead(lnk);

        Assert.NotNull(info);
        Assert.True(info!.RunAsAdministrator);
    }

    [Fact]
    public void AnOrdinaryShortcutIsNotMarkedRunAsAdministrator()
    {
        var target = MakeFile("normal.exe");
        var lnk = Lnk("normal.lnk");
        ShellShortcut.Create(target, lnk);

        Assert.False(ShellShortcut.TryRead(lnk)!.RunAsAdministrator);
    }

    [Theory]
    [InlineData(ShellShortcut.ShowNormal)]
    [InlineData(ShellShortcut.ShowMaximized)]
    [InlineData(ShellShortcut.ShowMinimized)]
    public void ReadsBackTheWindowStateTheShortcutAsksFor(int showCommand)
    {
        var target = MakeFile("windowed.exe");
        var lnk = Lnk($"windowed{showCommand}.lnk");
        ShellShortcut.Create(target, lnk, showCommand: showCommand);

        Assert.Equal(showCommand, ShellShortcut.TryRead(lnk)!.ShowCommand);
    }

    [Fact]
    public void ResolvesAShortcutPointingAtAFolder()
    {
        var folder = Path.Combine(_dir, "some folder");
        Directory.CreateDirectory(folder);
        var lnk = Lnk("folder.lnk");
        ShellShortcut.Create(folder, lnk);

        var info = ShellShortcut.TryRead(lnk);

        Assert.NotNull(info);
        Assert.Equal(folder, info!.TargetPath, ignoreCase: true);
    }

    [Fact]
    public void NonShortcutInputsReadAsNull()
    {
        Assert.Null(ShellShortcut.TryRead(MakeFile("notalink.exe")));
        Assert.Null(ShellShortcut.TryRead(Path.Combine(_dir, "missing.lnk")));
        Assert.Null(ShellShortcut.TryRead(null));
        Assert.Null(ShellShortcut.TryRead(""));
        Assert.Null(ShellShortcut.TryRead("   "));
    }

    [Fact]
    public void ACorruptShortcutReadsAsNullInsteadOfThrowing()
    {
        var lnk = Lnk("corrupt.lnk");
        File.WriteAllText(lnk, "this is definitely not a shell link");

        Assert.Null(ShellShortcut.TryRead(lnk));
    }

    [Fact]
    public void ResolveTargetPathWalksThroughAShortcutAndLeavesOtherPathsAlone()
    {
        var target = MakeFile("walk.exe");
        var lnk = Lnk("walk.lnk");
        ShellShortcut.Create(target, lnk);

        Assert.Equal(target, ShellShortcut.ResolveTargetPath(lnk), ignoreCase: true);
        // Not a shortcut, or unreadable: the input comes back untouched so callers can keep
        // treating it as the target.
        Assert.Equal(target, ShellShortcut.ResolveTargetPath(target), ignoreCase: true);
        Assert.Equal(@"C:\Windows\notepad.exe", ShellShortcut.ResolveTargetPath(@"C:\Windows\notepad.exe"));
    }
}
