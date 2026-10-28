using System.IO;
using AresToys.App.Services.NewPlus;
using Xunit;

namespace AresToys.App.Tests;

public sealed class NewPlusTemplatesTests
{
    private static readonly DateTime Moment = new(2026, 3, 7, 15, 4, 9, 123);

    [Theory]
    [InlineData("01. First entry.txt", false, "First entry.txt")]
    [InlineData("02. Second entry", true, "Second entry")]
    [InlineData("03 Third entry", true, "Third entry")]
    [InlineData("04 Fourth entry.txt", false, "Fourth entry.txt")]
    [InlineData("05.Fifth entry.txt", false, "Fifth entry.txt")]
    [InlineData("001231", true, "001231")]
    [InlineData("001231.txt", false, "001231.txt")]
    [InlineData("13. 0123456789012345.txt", false, "0123456789012345.txt")]
    [InlineData("0123.Name", true, "Name")]
    [InlineData("Report.txt", false, "Report.txt")]
    public void RemoveStartingDigits_FollowsPowerToysCases(string name, bool isDirectory, string expected)
        => Assert.Equal(expected, NewPlusTemplates.RemoveStartingDigits(name, isDirectory));

    [Fact]
    public void DateTime_TokensResolve_LongestFirst()
    {
        Assert.Equal("2026-03-07", NewPlusTemplates.ResolveDateTime("$YYYY-$MM-$DD", Moment));
        Assert.Equal("26", NewPlusTemplates.ResolveDateTime("$YY", Moment));
        Assert.Equal("15.04.09", NewPlusTemplates.ResolveDateTime("$hh.$mm.$ss", Moment));
        // HH is the 12 hour clock, hh the 24 hour one.
        Assert.Equal("03 PM", NewPlusTemplates.ResolveDateTime("$HH $TT", Moment));
        Assert.Equal("123", NewPlusTemplates.ResolveDateTime("$fff", Moment));
    }

    [Fact]
    public void DoubleDollar_EscapesAToken()
        => Assert.Equal("$$YYYY 2026", NewPlusTemplates.ResolveDateTime("$$YYYY $YYYY", Moment));

    [Fact]
    public void ResolveFileName_ReplacesParentAndForbiddenChars()
    {
        Assert.Equal("Notes of Projects", NewPlusTemplates.ResolveFileName("Notes of $PARENT_FOLDER_NAME", "Projects", Moment));
        Assert.Equal("a b", NewPlusTemplates.ResolveFileName("a:b", string.Empty, Moment));
    }

    [Fact]
    public void ResolveEnvironment_KnownVariableReplaced_UnknownKept()
    {
        Environment.SetEnvironmentVariable("ARESTOYS_NEWPLUS_TEST", "value");
        try
        {
            Assert.Equal("a value b", NewPlusTemplates.ResolveEnvironment("a %aRESTOYS_newplus_test% b"));
            Assert.Equal("%NO_SUCH_VARIABLE_XYZ%", NewPlusTemplates.ResolveEnvironment("%NO_SUCH_VARIABLE_XYZ%"));
        }
        finally { Environment.SetEnvironmentVariable("ARESTOYS_NEWPLUS_TEST", null); }
    }

    [Fact]
    public void RepairQuotedFolderArg_FixesDriveRoot()
    {
        Assert.Equal(@"C:\", NewPlusTemplates.RepairQuotedFolderArg("C:\""));
        Assert.Equal(@"C:\", NewPlusTemplates.RepairQuotedFolderArg("C:"));
        Assert.Equal(@"D:\Work", NewPlusTemplates.RepairQuotedFolderArg(@"D:\Work"));
    }

    [Fact]
    public void ListMakeUniqueAndCreate_WorkOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "arestoys-newplus-" + Guid.NewGuid().ToString("N"));
        var templates = Path.Combine(root, "templates");
        var target = Path.Combine(root, "target");
        try
        {
            Directory.CreateDirectory(Path.Combine(templates, "02. Folder $YYYY"));
            File.WriteAllText(Path.Combine(templates, "02. Folder $YYYY", "Note $PARENT_FOLDER_NAME.txt"), "x");
            File.WriteAllText(Path.Combine(templates, "01. Doc.txt"), "hello");
            Directory.CreateDirectory(target);

            // Folders first, then files.
            var list = NewPlusTemplates.List(templates);
            Assert.Equal(["02. Folder $YYYY", "01. Doc.txt"], list.Select(Path.GetFileName));

            var settings = new NewPlusSettings(true, templates, HideFileExtension: true, HideStartingDigits: true,
                ReplaceVariables: true, HideBuiltInNew: false);
            Assert.Equal("Doc", NewPlusTemplates.MenuTitle(list[1], settings, Moment));
            Assert.Equal("Folder 2026", NewPlusTemplates.MenuTitle(list[0], settings, Moment));

            var first = NewPlusService.Create(list[1], target, settings);
            var second = NewPlusService.Create(list[1], target, settings);
            Assert.Equal(Path.Combine(target, "Doc.txt"), first);
            Assert.Equal(Path.Combine(target, "Doc (1).txt"), second);

            var folder = NewPlusService.Create(list[0], target, settings);
            Assert.Equal($"Folder {DateTime.Now.Year}", Path.GetFileName(folder));
            Assert.True(File.Exists(Path.Combine(folder, $"Note Folder {DateTime.Now.Year}.txt")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ } }
    }
}
