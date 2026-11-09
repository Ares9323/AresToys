using AresToys.App.Services.Launcher;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Covers the AppUserModelID → shell-parsing-name normalisation. The bug this guards
/// against: dragging a packaged (MSIX/UWP) app off the Start menu hands the launcher an AUMID
/// (<c>5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App</c>) through DataFormats.FileDrop instead of
/// a filesystem path. Stored verbatim, ShellExecute fails it with ERROR_FILE_NOT_FOUND and the
/// shell can't resolve an icon for it either — the cell looks mapped but does nothing.</summary>
public sealed class PackagedAppPathTests
{
    private const string WhatsApp = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App";
    private const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Theory]
    [InlineData(WhatsApp)]
    [InlineData(Calculator)]
    [InlineData("Microsoft.Microsoft3DViewer_8wekyb3d8bbwe!Microsoft.Microsoft3DViewer")]
    [InlineData("Claude_pzs8sxrjxfjjc!Claude")]
    [InlineData("MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy!ClickToDoApp")]
    public void RecognisesRealAppUserModelIds(string aumid)
    {
        Assert.True(PackagedAppPath.LooksLikeAppUserModelId(aumid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Users\Me\Desktop\Wow! great_app.lnk")]           // '!' in a real path, has a drive + separators
    [InlineData(@"\\server\share\tool_v2!beta.exe")]                  // UNC
    [InlineData("https://example.com/a_b!c")]                         // URL
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]  // already normalised
    [InlineData("%WINDIR%\\system32\\calc.exe")]
    [InlineData("noseparator_but_no_bang.exe")]                       // no '!'
    [InlineData("nounderscore!App")]                                  // PFN always carries '_<publisherId>'
    [InlineData("two_bangs!App!Extra")]                                // AUMID has exactly one '!'
    [InlineData("_leading!App")]                                       // empty package name
    [InlineData("pkg_pub!")]                                           // empty app id
    [InlineData("!pkg_pub")]                                           // empty package family name
    public void RejectsEverythingThatIsNotAnAppUserModelId(string? candidate)
    {
        Assert.False(PackagedAppPath.LooksLikeAppUserModelId(candidate));
    }

    [Fact]
    public void NormalizeTurnsAnAumidIntoAShellParsingName()
    {
        Assert.Equal(@"shell:AppsFolder\" + WhatsApp, PackagedAppPath.Normalize(WhatsApp));
    }

    [Fact]
    public void NormalizeTrimsSurroundingWhitespace()
    {
        Assert.Equal(@"shell:AppsFolder\" + Calculator, PackagedAppPath.Normalize("  " + Calculator + "  "));
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("https://example.com")]
    [InlineData("%WINDIR%\\system32\\calc.exe")]
    [InlineData("")]
    public void NormalizeLeavesOrdinaryTargetsAlone(string raw)
    {
        Assert.Equal(raw, PackagedAppPath.Normalize(raw));
    }

    [Fact]
    public void NormalizeIsIdempotent()
    {
        var once = PackagedAppPath.Normalize(WhatsApp);
        Assert.Equal(once, PackagedAppPath.Normalize(once));
    }

    [Fact]
    public void NormalizeHandlesNull()
    {
        Assert.Equal(string.Empty, PackagedAppPath.Normalize(null));
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData(@"SHELL:APPSFOLDER\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]  // shell: paths are case-insensitive
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData("shell:Downloads", false)]
    [InlineData(null, false)]
    public void IsAppsFolderPathDetectsTheNormalisedForm(string? path, bool expected)
    {
        Assert.Equal(expected, PackagedAppPath.IsAppsFolderPath(path));
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\" + WhatsApp, WhatsApp)]
    [InlineData(WhatsApp, WhatsApp)]
    [InlineData(@"C:\Windows\notepad.exe", null)]
    [InlineData(null, null)]
    public void ExtractsTheAumidFromEitherForm(string? path, string? expected)
    {
        Assert.Equal(expected, PackagedAppPath.TryExtractAppUserModelId(path));
    }

    [Theory]
    // Fallback label when the shell can't hand us a display name: package name without the
    // publisher id, and without the reverse-DNS prefix Store packages carry.
    [InlineData(WhatsApp, "WhatsAppDesktop")]
    [InlineData(Calculator, "WindowsCalculator")]
    [InlineData("Claude_pzs8sxrjxfjjc!Claude", "Claude")]
    [InlineData(@"shell:AppsFolder\" + Calculator, "WindowsCalculator")]
    [InlineData(@"C:\Windows\notepad.exe", null)]
    public void DerivesAReadableFallbackLabel(string path, string? expected)
    {
        Assert.Equal(expected, PackagedAppPath.FallbackLabel(path));
    }
}
