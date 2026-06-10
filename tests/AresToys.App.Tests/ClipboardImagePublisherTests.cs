using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.App.Services;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>
/// Regression for the Discord paste fix: <see cref="ClipboardImagePublisher.SetPng"/> used to
/// decide whether to publish CF_BITMAP purely on the PNG IHDR colourType byte. WIC encodes
/// every <c>Format32bppArgb</c>-sourced image as colourType=6 (RGBA) even when every alpha
/// byte is 255 — so desktop screenshots (region / window / monitor capture all use
/// Format32bppArgb) were tagged "ambiguous" by the cheap header check, CF_BITMAP was
/// suppressed, and Discord (which doesn't read the registered "PNG" clipboard format) pasted
/// nothing. The authoritative answer is to scan the actual alpha channel; these tests pin
/// the scanner's behaviour against the canonical opacity scenarios.
/// </summary>
public class ClipboardImagePublisherTests
{
    private static BitmapSource MakeBgra(int width, int height, byte[] bgra)
    {
        var stride = width * 4;
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, palette: null, bgra, stride);
    }

    [Fact]
    public void BitmapHasAnyTransparentPixel_AllOpaque_ReturnsFalse()
    {
        // 2×2 fully-opaque BGRA. Mirrors the typical desktop screenshot — every alpha byte
        // is 0xFF; the scanner must NOT report transparency or we'd suppress CF_BITMAP
        // and break paste-into-Discord for this whole class of capture.
        var pixels = new byte[]
        {
            0x10, 0x20, 0x30, 0xFF,   0x40, 0x50, 0x60, 0xFF,
            0x70, 0x80, 0x90, 0xFF,   0xA0, 0xB0, 0xC0, 0xFF,
        };
        var bmp = MakeBgra(2, 2, pixels);
        Assert.False(ClipboardImagePublisher.BitmapHasAnyTransparentPixel(bmp));
    }

    [Fact]
    public void BitmapHasAnyTransparentPixel_SingleSemiTransparentPixel_ReturnsTrue()
    {
        // One pixel at alpha=0x80 (50%). Scanner must short-circuit and return true —
        // this is the case where CF_BITMAP would flatten the alpha to a solid bg, so
        // we MUST skip the CF_BITMAP publish and force consumers onto the PNG path.
        var pixels = new byte[]
        {
            0x10, 0x20, 0x30, 0xFF,   0x40, 0x50, 0x60, 0xFF,
            0x70, 0x80, 0x90, 0xFF,   0xA0, 0xB0, 0xC0, 0x80,
        };
        var bmp = MakeBgra(2, 2, pixels);
        Assert.True(ClipboardImagePublisher.BitmapHasAnyTransparentPixel(bmp));
    }

    [Fact]
    public void BitmapHasAnyTransparentPixel_FirstPixelTransparent_ReturnsTrueWithoutScanningRest()
    {
        // Short-circuit smoke test: a transparent first pixel followed by enough rows to
        // make full-scan vs short-circuit observable. Behaviour-wise we only assert the
        // return value (the perf claim is in the method's doc comment, not enforceable
        // from a unit test without a stopwatch). Still useful as a baseline.
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 0xFF; // default opaque
        }
        pixels[3] = 0x00; // first pixel's alpha
        var bmp = MakeBgra(8, 8, pixels);
        Assert.True(ClipboardImagePublisher.BitmapHasAnyTransparentPixel(bmp));
    }

    [Fact]
    public void PngHasAlphaFlag_RgbColorType_ReturnsFalse()
    {
        // Hand-crafted minimal PNG header bytes up to and including the colourType byte
        // (offset 25). The scanner only inspects that one byte, so the surrounding chunks
        // can be junk for the purposes of this test.
        var png = new byte[26];
        png[25] = 2; // colour type 2 = RGB (no alpha channel)
        Assert.False(ClipboardImagePublisher.PngHasAlphaFlag(png));
    }

    [Fact]
    public void PngHasAlphaFlag_RgbaColorType_ReturnsTrue()
    {
        var png = new byte[26];
        png[25] = 6; // colour type 6 = RGBA — header says alpha might be present
        Assert.True(ClipboardImagePublisher.PngHasAlphaFlag(png));
    }

    [Fact]
    public void PngHasAlphaFlag_TooShort_ReturnsTrue()
    {
        // < 26 bytes can't have an IHDR colourType — conservative branch returns true so
        // SetPng falls into the pixel-scan path rather than publishing a CF_BITMAP that
        // might mis-render an alpha image.
        Assert.True(ClipboardImagePublisher.PngHasAlphaFlag(new byte[] { 1, 2, 3 }));
    }
}
