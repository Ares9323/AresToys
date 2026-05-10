using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.Editor.Model;
using AresToys.Editor.ViewModels;

namespace AresToys.Editor.Commands;

/// <summary>Apply N pending crop rectangles as a composite: bbox = union of all rects,
/// output image keeps source pixels inside any rect, everything else transparent. Mirrors
/// ShareX's multi-region capture output. Single-rect input collapses to a simple crop.
/// <para>Undoable: stores the pre-apply source bytes + shape list and restores on Undo.
/// Shapes are translated by (-bbox.X, -bbox.Y) and dropped if their bbox falls fully
/// outside the new canvas, same rule the legacy single-rect <see cref="CropCommand"/>
/// uses.</para></summary>
public sealed class MultiCropCommand : IEditorCommand
{
    private readonly EditorViewModel _vm;
    private readonly IReadOnlyList<CropRect> _crops;
    private byte[]? _oldPng;
    private List<Shape>? _oldShapes;

    public MultiCropCommand(EditorViewModel vm, IReadOnlyList<CropRect> crops)
    {
        _vm = vm;
        _crops = crops;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        if (_crops.Count == 0) return;
        _oldPng = _vm.SourcePngBytes;
        _oldShapes = [.. shapes];

        // Bbox = union of all rects. Defines the new canvas dimensions.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var c in _crops)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X + c.Width > maxX)  maxX = c.X + c.Width;
            if (c.Y + c.Height > maxY) maxY = c.Y + c.Height;
        }
        var bboxX = (int)Math.Floor(minX);
        var bboxY = (int)Math.Floor(minY);
        var bboxW = Math.Max(1, (int)Math.Ceiling(maxX - bboxX));
        var bboxH = Math.Max(1, (int)Math.Ceiling(maxY - bboxY));

        var newPng = BuildCompositePng(_oldPng, bboxX, bboxY, bboxW, bboxH, _crops);
        if (newPng is null) return;

        _vm.SourcePngBytes = newPng;
        shapes.Clear();
        foreach (var s in _oldShapes)
        {
            var translated = TranslateShape(s, -bboxX, -bboxY);
            if (BoundsIntersect(translated, bboxW, bboxH)) shapes.Add(translated);
        }
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        if (_oldPng is null || _oldShapes is null) return;
        _vm.SourcePngBytes = _oldPng;
        shapes.Clear();
        foreach (var s in _oldShapes) shapes.Add(s);
    }

    /// <summary>Build the composite PNG: a transparent BGRA bitmap of bbox dimensions, with
    /// each crop rect's pixel content from the original source painted into it at the
    /// rect's bbox-relative offset. Where rects overlap, the later draw wins (idempotent
    /// since they sample the same source pixels). Where no rect covers a pixel, alpha=0.</summary>
    private static byte[]? BuildCompositePng(byte[] sourceBytes, int bboxX, int bboxY, int bboxW, int bboxH, IReadOnlyList<CropRect> crops)
    {
        if (sourceBytes.Length == 0) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(sourceBytes);
        bmp.EndInit();
        bmp.Freeze();

        // Render via WPF: Drawing on a DrawingContext into a RenderTargetBitmap. Each rect
        // becomes a CroppedBitmap (zero-copy view into the source) drawn at its target offset.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Background stays transparent — RenderTargetBitmap defaults to transparent
            // black, which we want for the "outside any rect" pixels.
            foreach (var c in crops)
            {
                var sx = Math.Max(0, (int)Math.Round(c.X));
                var sy = Math.Max(0, (int)Math.Round(c.Y));
                var sw = Math.Max(1, Math.Min((int)Math.Round(c.Width),  bmp.PixelWidth  - sx));
                var sh = Math.Max(1, Math.Min((int)Math.Round(c.Height), bmp.PixelHeight - sy));
                if (sw <= 0 || sh <= 0) continue;
                var sourceCrop = new CroppedBitmap(bmp, new Int32Rect(sx, sy, sw, sh));
                var dstX = sx - bboxX;
                var dstY = sy - bboxY;
                dc.DrawImage(sourceCrop, new Rect(dstX, dstY, sw, sh));
            }
        }

        var rtb = new RenderTargetBitmap(bboxW, bboxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static Shape TranslateShape(Shape s, double dx, double dy) => s switch
    {
        RectangleShape r => r with { X = r.X + dx, Y = r.Y + dy },
        EllipseShape e => e with { X = e.X + dx, Y = e.Y + dy },
        LineShape l => l with { FromX = l.FromX + dx, FromY = l.FromY + dy, ToX = l.ToX + dx, ToY = l.ToY + dy },
        FreehandShape f => f with { Points = f.Points.Select(p => (p.X + dx, p.Y + dy)).ToList() },
        TextShape t => t with { X = t.X + dx, Y = t.Y + dy },
        StepCounterShape c => c with { CenterX = c.CenterX + dx, CenterY = c.CenterY + dy },
        BlurShape b => b with { X = b.X + dx, Y = b.Y + dy },
        PixelateShape p => p with { X = p.X + dx, Y = p.Y + dy },
        SpotlightShape sp => sp with { X = sp.X + dx, Y = sp.Y + dy },
        _ => s
    };

    private static bool BoundsIntersect(Shape s, double w, double h)
    {
        var (x, y, sw, sh) = ApproximateBounds(s);
        return !(x + sw < 0 || x > w || y + sh < 0 || y > h);
    }

    private static (double X, double Y, double W, double H) ApproximateBounds(Shape shape) => shape switch
    {
        RectangleShape r => (r.X, r.Y, r.Width, r.Height),
        EllipseShape e => (e.X, e.Y, e.Width, e.Height),
        LineShape l => (Math.Min(l.FromX, l.ToX), Math.Min(l.FromY, l.ToY), Math.Abs(l.ToX - l.FromX), Math.Abs(l.ToY - l.FromY)),
        FreehandShape f when f.Points.Count > 0 => (f.Points.Min(p => p.X), f.Points.Min(p => p.Y),
            f.Points.Max(p => p.X) - f.Points.Min(p => p.X), f.Points.Max(p => p.Y) - f.Points.Min(p => p.Y)),
        TextShape t => (t.X, t.Y, t.Width, t.Height),
        StepCounterShape c => (c.CenterX - c.Radius, c.CenterY - c.Radius, c.Radius * 2, c.Radius * 2),
        BlurShape b => (b.X, b.Y, b.Width, b.Height),
        PixelateShape p => (p.X, p.Y, p.Width, p.Height),
        SpotlightShape sp => (sp.X, sp.Y, sp.Width, sp.Height),
        _ => (0, 0, 0, 0)
    };
}
