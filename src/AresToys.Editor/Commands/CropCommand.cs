using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AresToys.Editor.Model;
using AresToys.Editor.ViewModels;

namespace AresToys.Editor.Commands;

/// <summary>Destructive crop: replaces the source PNG with a sub-image and translates all shapes
/// by (-cropX, -cropY). Shapes whose bbox falls fully outside the new canvas are dropped on apply
/// and restored on undo.</summary>
public sealed class CropCommand : IEditorCommand
{
    private readonly EditorViewModel _vm;
    private readonly int _cropX, _cropY, _cropW, _cropH;
    private byte[]? _oldPng;
    private List<Shape>? _oldShapes;

    public CropCommand(EditorViewModel vm, int cropX, int cropY, int cropW, int cropH)
    {
        _vm = vm;
        _cropX = cropX; _cropY = cropY; _cropW = cropW; _cropH = cropH;
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        _oldPng = _vm.SourcePngBytes;
        _oldShapes = [.. shapes];

        var newPng = CropPng(_oldPng, _cropX, _cropY, _cropW, _cropH);
        if (newPng is null) return;

        _vm.SourcePngBytes = newPng;
        shapes.Clear();
        foreach (var s in _oldShapes)
        {
            var translated = TranslateShape(s, -_cropX, -_cropY);
            if (BoundsIntersect(translated, _cropW, _cropH)) shapes.Add(translated);
        }
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        if (_oldPng is null || _oldShapes is null) return;
        _vm.SourcePngBytes = _oldPng;
        shapes.Clear();
        foreach (var s in _oldShapes) shapes.Add(s);
    }

    private static byte[]? CropPng(byte[] pngBytes, int x, int y, int w, int h)
    {
        if (pngBytes.Length == 0) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(pngBytes);
        bmp.EndInit();
        bmp.Freeze();

        var cx = Math.Max(0, Math.Min(x, bmp.PixelWidth - 1));
        var cy = Math.Max(0, Math.Min(y, bmp.PixelHeight - 1));
        var cw = Math.Max(1, Math.Min(w, bmp.PixelWidth - cx));
        var ch = Math.Max(1, Math.Min(h, bmp.PixelHeight - cy));

        var cropped = new CroppedBitmap(bmp, new Int32Rect(cx, cy, cw, ch));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
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

    /// <summary>True if the shape's bbox intersects the new canvas [0, 0, w, h].</summary>
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
