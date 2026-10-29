using System.IO;
using AresToys.App.Services.NewPlus;
using Xunit;

namespace AresToys.App.Tests;

public class NewPlusHeadlessTests
{
    [Fact]
    public void FullArgumentList_WithBothFlags_IsParsed()
    {
        string[] args = ["--newplus", @"C:\Templates\01. Doc.txt", @"D:\Work", "--np-hide-digits", "--np-replace-vars"];

        Assert.True(NewPlusHeadless.TryParse(args, out var request));
        Assert.Equal(@"C:\Templates\01. Doc.txt", request.Template);
        Assert.Equal(@"D:\Work", request.Folder);
        Assert.True(request.HideStartingDigits);
        Assert.True(request.ReplaceVariables);
    }

    [Fact]
    public void WithoutFlags_BothOptionsAreOff()
    {
        Assert.True(NewPlusHeadless.TryParse(["--newplus", @"C:\T\a.txt", @"D:\Work"], out var request));
        Assert.False(request.HideStartingDigits);
        Assert.False(request.ReplaceVariables);
    }

    [Fact]
    public void DriveRoot_ArrivingWithTheClosingQuote_IsRepaired()
    {
        // "%V" for a drive root is C:\ and the backslash escapes the closing quote: the arg is C:"
        Assert.True(NewPlusHeadless.TryParse(["--newplus", @"C:\T\a.txt", "C:\"", "--np-replace-vars"], out var request));
        Assert.Equal(@"C:\", request.Folder);
        Assert.True(request.ReplaceVariables);
    }

    [Fact]
    public void WithoutTheSwitch_ItIsNotARequest()
    {
        Assert.False(NewPlusHeadless.TryParse(["--upload", @"C:\a.png"], out _));
        Assert.False(NewPlusHeadless.TryRun([]));
    }

    [Fact]
    public void MissingFolder_ItIsNotARequest()
    {
        Assert.False(NewPlusHeadless.TryParse(["--newplus", @"C:\T\a.txt"], out _));
    }

    [Fact]
    public void Create_CopiesTheTemplateIntoTheFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "arestoys-newplus-headless-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var template = Path.Combine(root, "Note.txt");
            File.WriteAllText(template, "hello");

            var settings = new NewPlusSettings(true, string.Empty, false, false, false, false);
            var created = NewPlusService.Create(template, target, settings);

            Assert.Equal(Path.Combine(target, "Note.txt"), created);
            Assert.Equal("hello", File.ReadAllText(created));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ } }
    }
}
