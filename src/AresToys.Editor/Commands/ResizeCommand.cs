using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.Editor.Model;
using AresToys.Editor.ViewModels;

namespace AresToys.Editor.Commands;

/// <summary>Destructive resize: replaces the source PNG with a scaled version and scales all shapes
/// proportionally. Stores old PNG + old shapes for undo.</summary>
public sealed class ResizeCommand : IEditorCommand
{
    private readonly EditorViewModel _vm;
    private readonly int _newW, _newH;
    private byte[]? _oldPng;
    private List<Shape>? _oldShapes;

    public ResizeCommand(EditorViewModel vm, int newWidth, int newHeight)
    {
        _vm = vm;
        _newW = Math.Max(1, newWidth);
        _newH = Math.Max(1, newHeight);
    }

    public void Apply(ObservableCollection<Shape> shapes)
    {
        _oldPng = _vm.SourcePngBytes;
        _oldShapes = [.. shapes];

        var (resized, sx, sy) = ResizePng(_oldPng, _newW, _newH);
        if (resized is null) return;

        _vm.SourcePngBytes = resized;
        shapes.Clear();
        foreach (var s in _oldShapes) shapes.Add(ScaleShape(s, sx, sy));
    }

    public void Undo(ObservableCollection<Shape> shapes)
    {
        if (_oldPng is null || _oldShapes is null) return;
        _vm.SourcePngBytes = _oldPng;
        shapes.Clear();
        foreach (var s in _oldShapes) shapes.Add(s);
    }

    private static (byte[]? Png, double ScaleX, double ScaleY) ResizePng(byte[] pngBytes, int newW, int newH)
    {
        if (pngBytes.Length == 0) return (null, 1, 1);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(pngBytes);
        bmp.EndInit();
        bmp.Freeze();

        var sx = (double)newW / bmp.PixelWidth;
        var sy = (double)newH / bmp.PixelHeight;
        var scaled = new TransformedBitmap(bmp, new ScaleTransform(sx, sy));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), sx, sy);
    }

    private static Shape ScaleShape(Shape s, double sx, double sy) => s switch
    {
        RectangleShape r => r with { X = r.X * sx, Y = r.Y * sy, Width = r.Width * sx, Height = r.Height * sy, StrokeWidth = r.StrokeWidth * AvgScale(sx, sy) },
        EllipseShape e => e with { X = e.X * sx, Y = e.Y * sy, Width = e.Width * sx, Height = e.Height * sy, StrokeWidth = e.StrokeWidth * AvgScale(sx, sy) },
        // Scale the bend offset alongside the endpoints so a curved line/arrow keeps the same
        // visual curvature relative to its segment after a resize.
        LineShape l => l with { FromX = l.FromX * sx, FromY = l.FromY * sy, ToX = l.ToX * sx, ToY = l.ToY * sy, ControlOffsetX = l.ControlOffsetX * sx, ControlOffsetY = l.ControlOffsetY * sy, StrokeWidth = l.StrokeWidth * AvgScale(sx, sy) },
        FreehandShape f => f with { Points = f.Points.Select(p => (p.X * sx, p.Y * sy)).ToList(), StrokeWidth = f.StrokeWidth * AvgScale(sx, sy) },
        // Canvas-resize scales the text frame's box dimensions AND the font so the visible
        // proportions match the pre-resize render. (Grip-resize is different — that one only
        // changes Width/Height to reflow the text, leaving the font alone.)
        TextShape t => t with { X = t.X * sx, Y = t.Y * sy, Width = t.Width * sx, Height = t.Height * sy, Style = t.Style with { FontSize = t.Style.FontSize * AvgScale(sx, sy) } },
        // Radius rides on StrokeWidth (Radius = StrokeWidth * 10 inside StepCounterShape), so
        // scaling stroke automatically scales the disc — no separate Radius assignment.
        StepCounterShape c => c with { CenterX = c.CenterX * sx, CenterY = c.CenterY * sy, StrokeWidth = c.StrokeWidth * AvgScale(sx, sy) },
        BlurShape b => b with { X = b.X * sx, Y = b.Y * sy, Width = b.Width * sx, Height = b.Height * sy, Radius = b.Radius * AvgScale(sx, sy) },
        PixelateShape p => p with { X = p.X * sx, Y = p.Y * sy, Width = p.Width * sx, Height = p.Height * sy, BlockSize = (int)Math.Max(2, Math.Round(p.BlockSize * AvgScale(sx, sy))) },
        SpotlightShape sp => sp with { X = sp.X * sx, Y = sp.Y * sy, Width = sp.Width * sx, Height = sp.Height * sy, BlurRadius = sp.BlurRadius * AvgScale(sx, sy) },
        _ => s
    };

    private static double AvgScale(double sx, double sy) => (sx + sy) / 2;
}
