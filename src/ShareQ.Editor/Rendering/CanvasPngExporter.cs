using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareQ.Editor.Rendering;

public static class CanvasPngExporter
{
    /// <summary>Renders the visual tree rooted at <paramref name="root"/> to a PNG byte[] at the
    /// given PHYSICAL PIXEL size. <paramref name="dpiX"/> / <paramref name="dpiY"/> are the
    /// source bitmap's DPI metadata — pass them through so the RTB and the Measure/Arrange
    /// rectangle stay in sync.
    ///
    /// Why DPI matters: WPF measures in DIPs (96 DPI by convention). A high-DPI screenshot
    /// (e.g. captured on a 150% display) has PixelWidth=450 but its natural visual-tree width
    /// is only 300 DIPs (450 × 96/144). If we Measure at <c>Size(450, 800)</c> as if those
    /// were DIPs, the image fills only the leftmost 300/450 of the Arrange rect and the
    /// RenderTargetBitmap captures a thin vertical slice with empty space to the right —
    /// exactly the bug we're fixing here. Solution: convert pixel size → DIP size for the
    /// layout pass, and tell the RTB the same DPI so it rasterises at the requested pixel
    /// resolution.
    ///
    /// LayoutTransform is reset for the render so the visual tree paints at 1:1 (no zoom),
    /// then restored — keeps the user's zoom UI state intact while the export is canonical.</summary>
    public static byte[] Export(FrameworkElement root, int pixelWidth, int pixelHeight, double dpiX = 96, double dpiY = 96)
    {
        if (dpiX <= 0) dpiX = 96;
        if (dpiY <= 0) dpiY = 96;
        var dipWidth = pixelWidth * 96.0 / dpiX;
        var dipHeight = pixelHeight * 96.0 / dpiY;

        var savedTransform = root.LayoutTransform;
        root.LayoutTransform = Transform.Identity;
        try
        {
            var dipSize = new Size(dipWidth, dipHeight);
            root.Measure(dipSize);
            root.Arrange(new Rect(dipSize));
            root.UpdateLayout();

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            root.LayoutTransform = savedTransform;
            root.UpdateLayout();
        }
    }

    /// <summary>Legacy double-based overload kept for callers that compute size from layout
    /// values; rounds to int. New code should pass the source <see cref="BitmapSource.PixelWidth"/>
    /// directly to avoid sub-pixel rounding.</summary>
    public static byte[] Export(FrameworkElement root, double width, double height)
        => Export(root, (int)Math.Round(width), (int)Math.Round(height));
}
