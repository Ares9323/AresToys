using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Drawings;

/// <summary>How <see cref="DrawImageImageEffect.Size"/> is interpreted. Mirrors ShareX's
/// <c>DrawImageSizeMode</c> so .sxie packages round-trip without translation.</summary>
public enum DrawImageSizeMode
{
    /// <summary>Use the watermark's native pixel size; ignore Size.</summary>
    DontResize,
    /// <summary>Size is the literal pixel dimensions to resize to. -1 means "match canvas".
    /// One axis can be 0 to mean "auto-fit on aspect ratio".</summary>
    AbsoluteSize,
    /// <summary>Size is a percentage (0..100) of the watermark's own dimensions.</summary>
    PercentageOfWatermark,
    /// <summary>Size is a percentage (0..100) of the source image's dimensions.</summary>
    PercentageOfCanvas,
}

/// <summary>Compositing mode applied when blending the overlay onto the source. ShareX uses
/// SourceOver (alpha blend) by default and SourceCopy to overwrite pixels — both common in
/// .sxie templates that want sharp corner artwork on top of a coloured border.</summary>
public enum DrawImageCompositingMode
{
    SourceOver,
    SourceCopy,
}

/// <summary>Rotate-then-flip transform applied to the overlay before compositing. Mirrors
/// ShareX's <c>ImageRotateFlipType</c> (which itself wraps GDI+ <c>RotateFlipType</c>) so
/// .sxie presets round-trip without translating the int / name. Semantics: rotate clockwise
/// by the named degrees, then flip horizontally (FlipX) or vertically (FlipY) in the rotated
/// orientation. <see cref="None"/> is the no-op default.</summary>
public enum ImageRotateFlipType
{
    None = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
    FlipX = 4,
    Rotate90FlipX = 5,
    FlipY = 6,
    Rotate90FlipY = 7,
}

/// <summary>Effect inspired by ShareX. Composites
/// an external image onto the source bitmap (logo / watermark / border artwork). Supports
/// SizeMode (DontResize / AbsoluteSize / PercentageOfWatermark / PercentageOfCanvas), Tile
/// fill, Opacity, and CompositingMode (SourceOver alpha blend or SourceCopy overwrite).
/// <see cref="ImageLocation"/> can be either an absolute path or a path relative to the
/// .sxie package's extraction folder; the importer rewrites relative ones to absolute on load.</summary>
public sealed class DrawImageImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_image";
    public override string Name => "Image";

    public string ImageLocation { get; set; } = string.Empty;
    public TextPlacement Placement { get; set; } = TextPlacement.TopLeft;

    [EffectParameter(-2000, 2000, DisplayName = "Offset X")]
    public int OffsetX { get; set; }

    [EffectParameter(-2000, 2000, DisplayName = "Offset Y")]
    public int OffsetY { get; set; }

    public DrawImageSizeMode SizeMode { get; set; } = DrawImageSizeMode.DontResize;

    /// <summary>Width axis of the resized overlay. Interpretation depends on
    /// <see cref="SizeMode"/>: pixels for AbsoluteSize, percent for the two Percentage modes.
    /// 0 (with the other axis non-zero) keeps aspect ratio. ShareX's Point-string format
    /// <c>"W, H"</c> is parsed by <see cref="Serialization.EffectPropertyBinder"/> into this
    /// pair so .sxie presets land here without bespoke deserializer plumbing.</summary>
    [EffectParameter(-100, 4000, DisplayName = "Width")]
    public int Width { get; set; }

    [EffectParameter(-100, 4000, DisplayName = "Height")]
    public int Height { get; set; }

    /// <summary>Tile the overlay across the placement rectangle instead of stretching it.
    /// Used by texture-style watermarks (e.g. WumpusConfetti) where the source is a small
    /// tileable pattern.</summary>
    public bool Tile { get; set; }

    [EffectParameter(0, 100, DisplayName = "Opacity")]
    public int Opacity { get; set; } = 100;

    [EffectParameter(DisplayName = "Auto hide if out of bounds")]
    public bool AutoHide { get; set; }

    public DrawImageCompositingMode CompositingMode { get; set; } = DrawImageCompositingMode.SourceOver;

    /// <summary>Pre-transform applied to the overlay before resize / placement. ShareX exposes
    /// the same eight-value enum on its DrawImage editor; presets that ship a flipped logo
    /// (e.g. mirrored corner artwork) only render correctly when this is honoured.</summary>
    public ImageRotateFlipType RotateFlip { get; set; } = ImageRotateFlipType.None;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(ImageLocation) || !System.IO.File.Exists(ImageLocation)) return source.Copy();
        if (Opacity <= 0) return source.Copy();
        // Resize modes need at least one positive dimension; DontResize ignores Width/Height entirely.
        if (SizeMode != DrawImageSizeMode.DontResize && Width <= 0 && Height <= 0) return source.Copy();

        SKBitmap? overlay;
        try { overlay = SKBitmap.Decode(ImageLocation); }
        catch { return source.Copy(); }
        if (overlay is null) return source.Copy();

        try
        {
            // Pre-transform the overlay if the preset asks for rotate/flip. Done before the
            // resize step because Rotate90/270 swap the natural width/height — the percentage-
            // and absolute-size calculators need to see the post-rotation dimensions to land
            // where the .sxie author intended.
            if (RotateFlip != ImageRotateFlipType.None)
            {
                var rotated = ApplyRotateFlip(overlay, RotateFlip);
                overlay.Dispose();
                overlay = rotated;
            }

            var (resizedW, resizedH) = ResolveOverlaySize(overlay.Width, overlay.Height, source.Width, source.Height);
            if (resizedW != overlay.Width || resizedH != overlay.Height)
            {
                using var resized = overlay.Resize(new SKSizeI(resizedW, resizedH), SKSamplingOptions.Default);
                if (resized is null) return source.Copy();
                overlay.Dispose();
                overlay = resized.Copy();
            }

            var anchor = ResolveAnchor(source.Width, source.Height, overlay.Width, overlay.Height);

            if (AutoHide && (anchor.X < 0 || anchor.Y < 0
                || anchor.X + overlay.Width > source.Width || anchor.Y + overlay.Height > source.Height))
                return source.Copy();

            var result = source.Copy();
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)(255 * Opacity / 100)),
                // SourceCopy overwrites destination pixels (preserves overlay's alpha as-is —
                // useful for sharp corner sprites that should punch through whatever's underneath).
                BlendMode = CompositingMode == DrawImageCompositingMode.SourceCopy ? SKBlendMode.Src : SKBlendMode.SrcOver,
            };

            if (Tile && SizeMode == DrawImageSizeMode.DontResize)
            {
                // AresToys-specific UX kicker: ShareX's Tile fills "imageRectangle" — when
                // SizeMode=DontResize that rect is exactly one tile big, so Tile=on looks
                // identical to Tile=off and users assume the toggle is broken. Without an
                // explicit Size the natural mental model is "tile across the whole image", so
                // we override the fill rect to the full canvas in this case. .sxie round-trip
                // unaffected (we still serialize Tile / SizeMode / Width / Height verbatim).
                using var shader = SKShader.CreateBitmap(overlay,
                    SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                    SKMatrix.CreateTranslation(0, 0));
                paint.Shader = shader;
                canvas.DrawRect(new SKRect(0, 0, source.Width, source.Height), paint);
            }
            else if (Tile)
            {
                // Tile fills only the placement rectangle (anchor + resized overlay size) with
                // the bitmap repeating inside it — same shape as ShareX's TextureBrush+FillRectangle.
                // Without the tight rect bound, the tile would smear across the whole canvas
                // (e.g. MacOS9's 22-px-tall top strip would cover the entire image).
                using var shader = SKShader.CreateBitmap(overlay,
                    SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                    SKMatrix.CreateTranslation(anchor.X, anchor.Y));
                paint.Shader = shader;
                canvas.DrawRect(new SKRect(anchor.X, anchor.Y,
                                           anchor.X + overlay.Width,
                                           anchor.Y + overlay.Height), paint);
            }
            else
            {
                canvas.DrawBitmap(overlay, anchor.X, anchor.Y, paint);
            }
            return result;
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    /// <summary>Translate the SizeMode + Width/Height pair into actual pixel dimensions, using
    /// the canvas / overlay sizes as the percentage anchors when applicable. Mirrors ShareX's
    /// <c>DrawImage.Apply</c> resize block including the aspect-ratio fallback when one axis
    /// is left at 0.</summary>
    private (int Width, int Height) ResolveOverlaySize(int overlayW, int overlayH, int canvasW, int canvasH)
    {
        switch (SizeMode)
        {
            case DrawImageSizeMode.DontResize:
                return (overlayW, overlayH);
            case DrawImageSizeMode.AbsoluteSize:
            {
                // ShareX special-case: -1 means "match the canvas dimension exactly". Used by the
                // Windows*/MacOS9 borders where the side strip extends the full source width.
                var w = Width == -1 ? canvasW : Width;
                var h = Height == -1 ? canvasH : Height;
                return ApplyAspectRatio(w, h, overlayW, overlayH);
            }
            case DrawImageSizeMode.PercentageOfWatermark:
            {
                var w = (int)Math.Round(Width / 100f * overlayW);
                var h = (int)Math.Round(Height / 100f * overlayH);
                return ApplyAspectRatio(w, h, overlayW, overlayH);
            }
            case DrawImageSizeMode.PercentageOfCanvas:
            {
                var w = (int)Math.Round(Width / 100f * canvasW);
                var h = (int)Math.Round(Height / 100f * canvasH);
                return ApplyAspectRatio(w, h, overlayW, overlayH);
            }
            default:
                return (overlayW, overlayH);
        }
    }

    /// <summary>If only one axis is supplied (the other is 0), scale the missing one to keep
    /// the overlay's original aspect. Both 0 means "no resize"; both positive means "use those".</summary>
    private static (int Width, int Height) ApplyAspectRatio(int w, int h, int sourceW, int sourceH)
    {
        if (w <= 0 && h <= 0) return (sourceW, sourceH);
        if (w <= 0) w = (int)Math.Round(h * (sourceW / (float)sourceH));
        else if (h <= 0) h = (int)Math.Round(w * (sourceH / (float)sourceW));
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Apply a rotate-then-flip transform to <paramref name="src"/> and return a new
    /// bitmap. Rotation is clockwise around the centre; FlipX mirrors horizontally and FlipY
    /// vertically — both performed in the rotated orientation, matching GDI+
    /// <c>Image.RotateFlip</c> semantics so a ShareX preset's RotateFlip value produces the
    /// same pixel layout here. <see cref="ImageRotateFlipType.None"/> is filtered out by the
    /// caller so this method is only invoked for actual transforms.</summary>
    private static SKBitmap ApplyRotateFlip(SKBitmap src, ImageRotateFlipType type)
    {
        var swap = type is ImageRotateFlipType.Rotate90 or ImageRotateFlipType.Rotate270
                          or ImageRotateFlipType.Rotate90FlipX or ImageRotateFlipType.Rotate90FlipY;
        var dstW = swap ? src.Height : src.Width;
        var dstH = swap ? src.Width : src.Height;
        var dst = new SKBitmap(dstW, dstH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);

        var angle = type switch
        {
            ImageRotateFlipType.Rotate90 or ImageRotateFlipType.Rotate90FlipX or ImageRotateFlipType.Rotate90FlipY => 90f,
            ImageRotateFlipType.Rotate180 => 180f,
            ImageRotateFlipType.Rotate270 => 270f,
            _ => 0f,
        };
        var flipX = type is ImageRotateFlipType.FlipX or ImageRotateFlipType.Rotate90FlipX;
        var flipY = type is ImageRotateFlipType.FlipY or ImageRotateFlipType.Rotate90FlipY;

        // Compose the transform around the destination centre so the rotated image lands
        // anchored, then translate by the source half-extent to cover the whole bitmap.
        canvas.Translate(dstW / 2f, dstH / 2f);
        if (angle != 0f) canvas.RotateDegrees(angle);
        if (flipX || flipY) canvas.Scale(flipX ? -1f : 1f, flipY ? -1f : 1f);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    private SKPoint ResolveAnchor(int canvasW, int canvasH, int imageW, int imageH)
    {
        // Same compass-style sign convention as DrawTextEx: Bottom*/Right* placements push
        // the offset INWARD (subtract OffsetX/OffsetY) rather than outward, mirroring ShareX
        // Helpers.GetPosition exactly so .sxie offsets land where their authors intended.
        switch (Placement)
        {
            case TextPlacement.TopLeft:
                return new SKPoint(OffsetX, OffsetY);
            case TextPlacement.TopCenter:
                return new SKPoint((canvasW - imageW) / 2, OffsetY);
            case TextPlacement.TopRight:
                return new SKPoint(canvasW - imageW - OffsetX, OffsetY);
            case TextPlacement.MiddleLeft:
                return new SKPoint(OffsetX, (canvasH - imageH) / 2);
            case TextPlacement.MiddleCenter:
                return new SKPoint((canvasW - imageW) / 2, (canvasH - imageH) / 2);
            case TextPlacement.MiddleRight:
                return new SKPoint(canvasW - imageW - OffsetX, (canvasH - imageH) / 2);
            case TextPlacement.BottomLeft:
                return new SKPoint(OffsetX, canvasH - imageH - OffsetY);
            case TextPlacement.BottomCenter:
                return new SKPoint((canvasW - imageW) / 2, canvasH - imageH - OffsetY);
            case TextPlacement.BottomRight:
                return new SKPoint(canvasW - imageW - OffsetX, canvasH - imageH - OffsetY);
            default:
                return new SKPoint(OffsetX, OffsetY);
        }
    }
}
