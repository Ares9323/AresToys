using System.IO;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AresToys.App.Services.ImageEffects;

/// <summary>One-shot Skia → WPF bitmap converter. Encodes through PNG so the resulting
/// <see cref="BitmapImage"/> is fully decoded and frozen — safe to assign across threads
/// and to bind directly into XAML. PNG is fast enough at preview sizes (800×600 ≈ &lt;5 ms)
/// and avoids the BGRA / pixel-stride dance that copying through <c>WriteableBitmap</c>
/// would require.</summary>
public static class SkiaToWpfBitmap
{
    public static BitmapImage Convert(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;

        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = ms;
        result.EndInit();
        result.Freeze();
        return result;
    }
}
