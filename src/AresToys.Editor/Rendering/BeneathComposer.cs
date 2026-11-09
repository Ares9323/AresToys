using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AresToys.Editor.Model;

namespace AresToys.Editor.Rendering;

/// <summary>Produces "the pixels beneath shape i": the screenshot with shapes 0..i-1 drawn over
/// it, in the creation order of the shape list. Effects (Blur, Pixelate, Spotlight, Smart
/// Eraser) read this instead of the bare screenshot, so each one works on what is under it at
/// that point and whatever is placed later sits on top.
/// <para>Canvas units are source pixels, so the composite is a 96 DPI bitmap of exactly the
/// source's pixel size: one canvas unit is one pixel and no DIP/pixel conversion is needed when
/// an effect crops it by pixel rectangles.</para>
/// <para>Caching: a composite is keyed by the source and the reference list of the shapes below
/// it. Shape records are immutable (an edit replaces the instance), so reference identity is a
/// valid key. A miss starts from the longest cached prefix, so after a drag only the moving
/// shape and what follows it are rendered again. A shape list with no shape below the first
/// effect costs nothing: the source is returned as is.</para></summary>
public sealed class BeneathComposer
{
    private sealed class Entry(BitmapSource source, Shape[] prefix, BitmapSource bitmap)
    {
        public BitmapSource Source { get; } = source;
        public Shape[] Prefix { get; } = prefix;
        public BitmapSource Bitmap { get; } = bitmap;
        public bool Used { get; set; } = true;
    }

    private readonly Func<Shape, BitmapSource?, UIElement> _build;
    private readonly List<Entry> _entries = [];

    /// <param name="build">Builds the canvas element of a shape. Effects receive the bitmap
    /// beneath them; every other shape receives null.</param>
    public BeneathComposer(Func<Shape, BitmapSource?, UIElement> build) => _build = build;

    public static bool IsEffect(Shape s) =>
        s is BlurShape or PixelateShape or SpotlightShape or SmartEraserShape;

    /// <summary>The composite of <paramref name="source"/> and <c>shapes[0..count)</c>.</summary>
    public BitmapSource Beneath(BitmapSource source, IReadOnlyList<Shape> shapes, int count)
    {
        if (count <= 0) return source;
        _entries.RemoveAll(e => !ReferenceEquals(e.Source, source));

        Entry? best = null;
        foreach (var e in _entries)
        {
            if (e.Prefix.Length > count) continue;
            if (best is not null && e.Prefix.Length <= best.Prefix.Length) continue;
            if (IsPrefixOf(e.Prefix, shapes)) best = e;
        }
        if (best is not null) best.Used = true;
        if (best is not null && best.Prefix.Length == count) return best.Bitmap;

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var start = best?.Prefix.Length ?? 0;
        var host = new Canvas { Width = width, Height = height, ClipToBounds = true, UseLayoutRounding = true };
        var baseImage = new Image
        {
            Source = best?.Bitmap ?? source,
            Stretch = Stretch.Fill,
            Width = width,
            Height = height
        };
        RenderOptions.SetBitmapScalingMode(baseImage, BitmapScalingMode.NearestNeighbor);
        host.Children.Add(baseImage);
        for (var k = start; k < count; k++)
        {
            var shape = shapes[k];
            var under = IsEffect(shape) ? Beneath(source, shapes, k) : null;
            host.Children.Add(_build(shape, under));
        }
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();

        var prefix = new Shape[count];
        for (var k = 0; k < count; k++) prefix[k] = shapes[k];
        _entries.Add(new Entry(source, prefix, rtb));
        return rtb;
    }

    /// <summary>Drops the composites nobody asked for since the previous call. Call it once at the
    /// end of a full redraw: it keeps memory to the composites the canvas still uses.</summary>
    public void Trim()
    {
        _entries.RemoveAll(e => !e.Used);
        foreach (var e in _entries) e.Used = false;
    }

    private static bool IsPrefixOf(Shape[] prefix, IReadOnlyList<Shape> shapes)
    {
        for (var i = 0; i < prefix.Length; i++)
            if (!ReferenceEquals(prefix[i], shapes[i])) return false;
        return true;
    }
}
