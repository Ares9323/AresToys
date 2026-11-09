using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace AresToys.Capture;

[SupportedOSPlatform("windows")]
public sealed class BitBltCaptureSource : ICaptureSource
{
    public Task<CapturedImage> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.IsEmpty) throw new ArgumentException("Capture region must have positive size.", nameof(region));

        cancellationToken.ThrowIfCancellationRequested();

        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        return Task.FromResult(new CapturedImage(region.Width, region.Height, bytes));
    }
}
