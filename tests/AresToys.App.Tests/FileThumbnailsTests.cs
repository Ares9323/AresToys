using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.App.Services.ImageFiles;
using SkiaSharp;
using Xunit;

namespace AresToys.App.Tests;

public sealed class FileThumbnailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AresToysThumbTests_" + Guid.NewGuid().ToString("N"));

    public FileThumbnailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ParsePaths_SplitsCrLfAndLf_AndDropsBlanks()
    {
        var paths = FileThumbnails.ParsePaths("C:\\a.png\r\n\r\n  C:\\b.txt \nC:\\c.webp\n");
        Assert.Equal(["C:\\a.png", "C:\\b.txt", "C:\\c.webp"], paths);
        Assert.Empty(FileThumbnails.ParsePaths(null));
        Assert.Empty(FileThumbnails.ParsePaths("   "));
    }

    [Fact]
    public void FirstImagePath_SkipsNonImages_CaseInsensitive()
    {
        Assert.Equal("C:\\b.JPG", FileThumbnails.FirstImagePath(["C:\\a.txt", "C:\\b.JPG", "C:\\c.png"]));
        Assert.Null(FileThumbnails.FirstImagePath(["C:\\a.txt", "C:\\folder"]));
    }

    [Fact]
    public void IsWebP_RecognisesRiffWebpHeader()
    {
        var webp = EncodeSkia(4, 4, SKEncodedImageFormat.Webp, transparentLeftHalf: false);
        Assert.True(ImageDecoding.IsWebP(webp));
        Assert.False(ImageDecoding.IsWebP(EncodeSkia(4, 4, SKEncodedImageFormat.Png, transparentLeftHalf: false)));
        Assert.False(ImageDecoding.IsWebP("RIFF0000WAVE"u8));
        Assert.False(ImageDecoding.IsWebP([]));
    }

    [Theory]
    [InlineData(1000, 500, 96, 96, 48)]
    [InlineData(500, 1000, 96, 48, 96)]
    [InlineData(40, 30, 96, 40, 30)]
    [InlineData(5000, 10, 96, 96, 1)]
    public void FitWithin_CapsLongerSide_NeverUpscales(int w, int h, int max, int ew, int eh)
        => Assert.Equal((ew, eh), ImageDecoding.FitWithin(w, h, max));

    [Fact]
    public void TryGenerate_LargePng_ProducesThumbnailAtMaxSide()
    {
        var path = Write("big.png", EncodeSkia(1000, 500, SKEncodedImageFormat.Png, transparentLeftHalf: false));

        var thumb = FileThumbnails.TryGenerate([path]);

        var bmp = DecodePng(thumb);
        Assert.Equal(96, bmp.PixelWidth);
        Assert.Equal(48, bmp.PixelHeight);
    }

    [Fact]
    public void TryGenerate_UsesFirstExistingImage_SkippingTextAndMissingFiles()
    {
        var text = Write("notes.txt", "hello"u8.ToArray());
        var missing = Path.Combine(_dir, "gone.png");
        var tall = Write("tall.png", EncodeSkia(50, 200, SKEncodedImageFormat.Png, transparentLeftHalf: false));
        var wide = Write("wide.png", EncodeSkia(200, 50, SKEncodedImageFormat.Png, transparentLeftHalf: false));

        var bmp = DecodePng(FileThumbnails.TryGenerate([text, missing, tall, wide]));

        Assert.Equal(24, bmp.PixelWidth);
        Assert.Equal(96, bmp.PixelHeight);
    }

    [Fact]
    public void TryGenerate_NoReadableImage_ReturnsNull()
    {
        var corrupt = Write("broken.png", [1, 2, 3, 4, 5]);
        Assert.Null(FileThumbnails.TryGenerate([corrupt, Path.Combine(_dir, "missing.jpg")]));
        Assert.Null(FileThumbnails.TryGenerate([]));
    }

    [Fact]
    public void TryGenerate_TransparentWebP_KeepsAlpha()
    {
        var path = Write("alpha.webp", EncodeSkia(200, 100, SKEncodedImageFormat.Webp, transparentLeftHalf: true));

        var bmp = DecodePng(FileThumbnails.TryGenerate([path]));

        Assert.Equal(96, bmp.PixelWidth);
        Assert.Equal(48, bmp.PixelHeight);
        var pixels = ReadBgra(bmp);
        Assert.Equal(0, AlphaAt(pixels, bmp.PixelWidth, 5, 24));   // left half: transparent
        Assert.Equal(255, AlphaAt(pixels, bmp.PixelWidth, 90, 24)); // right half: opaque
    }

    [Fact]
    public void Decode_TransparentWebP_KeepsAlphaAtFullSize()
    {
        var bmp = ImageDecoding.Decode(EncodeSkia(20, 10, SKEncodedImageFormat.Webp, transparentLeftHalf: true));

        Assert.NotNull(bmp);
        Assert.Equal(20, bmp!.PixelWidth);
        var pixels = ReadBgra(bmp);
        Assert.Equal(0, AlphaAt(pixels, bmp.PixelWidth, 1, 5));
        Assert.Equal(255, AlphaAt(pixels, bmp.PixelWidth, 18, 5));
        Assert.Equal((20, 10), ImageDecoding.TryGetPixelSize(EncodeSkia(20, 10, SKEncodedImageFormat.Webp, transparentLeftHalf: true)));
    }

    [Fact]
    public void TryGenerateFromFile_DoesNotKeepTheFileLocked()
    {
        var path = Write("locked.png", EncodeSkia(300, 300, SKEncodedImageFormat.Png, transparentLeftHalf: false));

        Assert.NotNull(FileThumbnails.TryGenerateFromFile(path));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Loader_GeneratesOncePerItem_AndSkipsRowsWithoutImages()
    {
        var calls = 0;
        var ready = new TaskCompletionSource<(long, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new FileRowThumbnailLoader(
            (id, thumb) => ready.TrySetResult((id, thumb)),
            _ => { Interlocked.Increment(ref calls); return [42]; });

        loader.Request(7, ["C:\\doc.txt"]);
        loader.Request(5, ["C:\\pic.png"]);
        loader.Request(5, ["C:\\pic.png"]);

        var (gotId, gotThumb) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, gotId);
        Assert.Equal([42], gotThumb);
        await Task.Delay(100);
        Assert.Equal(1, calls);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] EncodeSkia(int w, int h, SKEncodedImageFormat format, bool transparentLeftHalf)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bmp.Erase(new SKColor(200, 30, 30, 255));
        if (transparentLeftHalf)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w / 2; x++)
                    bmp.SetPixel(x, y, SKColors.Transparent);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static BitmapSource DecodePng(byte[]? png)
    {
        Assert.NotNull(png);
        using var ms = new MemoryStream(png!);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static byte[] ReadBgra(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte AlphaAt(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4 + 3];
}
