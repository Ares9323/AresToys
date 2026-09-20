using System;
using System.IO;
using AresToys.App.ViewModels;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Dropping a path onto a workflow step's Path field has to end up with the same value
/// the launcher would store for the same drag: a Start-menu packaged app is only runnable in its
/// shell:AppsFolder form, and a shortcut has to go through the step's unwrap. These tests pin
/// that mapping — the drag plumbing itself (MainWindow's Preview handlers) can't be exercised
/// headless, but everything it decides lives here.</summary>
public sealed class StringParameterDropTests
{
    private static StringParameterEntry MakeEntry(
        StringPickerKind picker, Func<string, string>? onPathPicked = null) =>
        new("path", "Path", null, string.Empty, picker,
            options: null, isEditable: true,
            localizeOptionsAsEnum: false, localizeOptionsAsLauncherKey: false,
            localizeOptionsAsColorFormat: false, localizeOptionsAsSettingsTab: false,
            onChanged: (_, _) => { }, onPathPicked: onPathPicked);

    [Fact]
    public void AStartMenuPackagedAppIsStoredInItsRunnableForm()
    {
        var unwrapCalls = 0;
        var entry = MakeEntry(StringPickerKind.File, p => { unwrapCalls++; return p; });

        entry.ApplyDroppedPath("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App");

        Assert.Equal(@"shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", entry.Value);
        // The shortcut unwrap treats its input as a filesystem path; an AUMID isn't one, so it
        // must be short-circuited before reaching it.
        Assert.Equal(0, unwrapCalls);
    }

    [Fact]
    public void AnOrdinaryPathGoesThroughTheStepsShortcutUnwrap()
    {
        var entry = MakeEntry(StringPickerKind.File, _ => @"C:\Program Files\App\app.exe");

        entry.ApplyDroppedPath(@"C:\Users\Someone\Desktop\App.lnk");

        Assert.Equal(@"C:\Program Files\App\app.exe", entry.Value);
    }

    [Fact]
    public void WithoutAnUnwrapTheDroppedPathIsStoredVerbatim()
    {
        var entry = MakeEntry(StringPickerKind.File);

        entry.ApplyDroppedPath(@"  C:\Tools\thing.exe  ");

        Assert.Equal(@"C:\Tools\thing.exe", entry.Value);
    }

    [Fact]
    public void AFileDroppedOnAFolderFieldContributesItsFolder()
    {
        var file = Path.Combine(Path.GetTempPath(), $"arestoys-drop-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "x");
        try
        {
            var entry = MakeEntry(StringPickerKind.Folder);

            entry.ApplyDroppedPath(file);

            Assert.Equal(Path.GetDirectoryName(file), entry.Value);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AnEmptyDropLeavesTheFieldAlone()
    {
        var entry = MakeEntry(StringPickerKind.File);
        entry.ApplyDroppedPath(@"C:\Tools\thing.exe");

        entry.ApplyDroppedPath("   ");

        Assert.Equal(@"C:\Tools\thing.exe", entry.Value);
    }

    [Theory]
    [InlineData(StringPickerKind.File, true)]
    [InlineData(StringPickerKind.Folder, true)]
    [InlineData(StringPickerKind.FileOrFolder, true)]
    // Args / command lines / hotkey combos aren't filesystem targets: a dropped file there would
    // be a guess, so those fields keep refusing drops.
    [InlineData(StringPickerKind.None, false)]
    [InlineData(StringPickerKind.HotkeyCapture, false)]
    public void OnlyPathShapedParametersAcceptADrop(StringPickerKind picker, bool expected)
    {
        Assert.Equal(expected, MakeEntry(picker).AcceptsPathDrop);
    }
}
