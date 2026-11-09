using AresToys.App.Services.Launcher;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Icon resolution for packaged (MSIX/UWP) apps. These go through the shell's
/// parsing-name APIs, not the filesystem ones: <c>SHGetFileInfo</c> can't see a
/// <c>shell:AppsFolder\</c> target at all and answers with the generic unknown-file glyph, which
/// is exactly how a Store app's cell ends up looking blank. Environment-dependent — skipped when
/// the probe app isn't installed.</summary>
public sealed class PackagedAppIconTests
{
    private const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private static bool Installed => PackagedAppPath.TryGetDisplayName(Calculator) is not null;

    /// <summary>Fingerprint an icon so two resolutions can be compared for "same picture".</summary>
    private static string? Fingerprint(System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        if (bmp is null) return null;
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }

    [Fact]
    public void ResolvesAnIconForAPackagedApp()
    {
        if (!Installed) return;
        var icons = new IconService();

        Assert.NotNull(icons.GetIcon(Calculator));
    }

    [Fact]
    public void ThePackagedAppIconIsTheAppsOwnNotTheGenericUnknownFileGlyph()
    {
        if (!Installed) return;
        var icons = new IconService();

        var app = Fingerprint(icons.GetIcon(Calculator));
        // A path that resolves to nothing: the shell answers with its generic glyph. If the
        // packaged app comes back with the same picture, we never actually asked the shell
        // about the app.
        var generic = Fingerprint(icons.GetIcon(@"C:\definitely-not-here\nothing.qqqq"));

        Assert.NotNull(app);
        Assert.NotEqual(generic, app);
    }

    [Fact]
    public void TheSizedVariantAlsoResolvesTheAppsOwnIcon()
    {
        if (!Installed) return;
        var icons = new IconService();

        var app = Fingerprint(icons.GetIconAtSize(Calculator, 48));
        var generic = Fingerprint(icons.GetIconAtSize(@"C:\definitely-not-here\nothing.qqqq", 48));

        Assert.NotNull(app);
        Assert.NotEqual(generic, app);
    }

    [Fact]
    public void ABareAppUserModelIdResolvesTheSameAsItsNormalisedForm()
    {
        if (!Installed) return;
        var icons = new IconService();

        // Cells saved before the packaged-app fix hold the bare id; they must not render blank.
        Assert.Equal(
            Fingerprint(icons.GetIcon(PackagedAppPath.AppsFolderPrefix + Calculator)),
            Fingerprint(icons.GetIcon(Calculator)));
    }
}
