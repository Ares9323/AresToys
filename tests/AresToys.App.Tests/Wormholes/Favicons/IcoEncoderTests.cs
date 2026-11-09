using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.App.Services.Wormholes.Favicons;
using Xunit;

namespace AresToys.App.Tests.Wormholes.Favicons;

public class IcoEncoderTests
{
    [Fact]
    public void ToIco_AlreadyIco_PassesThrough()
    {
        var ico = new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0xDE, 0xAD };
        var result = IcoEncoder.ToIco(ico);
        Assert.Same(ico, result);
    }

    [Fact]
    public void ToIco_Garbage_ReturnsNull()
    {
        Assert.Null(IcoEncoder.ToIco(new byte[] { 1, 2, 3, 4, 5, 6 }));
        Assert.Null(IcoEncoder.ToIco(null));
        Assert.Null(IcoEncoder.ToIco(new byte[] { 1 }));
    }

    [Fact]
    public void ToIco_Png_ProducesValidIco()
    {
        var png = MakePng(16, 16);
        var ico = IcoEncoder.ToIco(png);

        Assert.NotNull(ico);
        Assert.True(IcoEncoder.IsIco(ico!));
        // One entry, image data offset = 22, embedded payload is the PNG we fed in.
        Assert.Equal(1, ico![4] | (ico[5] << 8)); // image count (little-endian)
        var offset = ico[18] | (ico[19] << 8) | (ico[20] << 16) | (ico[21] << 24);
        Assert.Equal(22, offset);
        Assert.True(ico.Length > 22, "ICO must carry the PNG payload after the header");
    }

    private static byte[] MakePng(int w, int h)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
