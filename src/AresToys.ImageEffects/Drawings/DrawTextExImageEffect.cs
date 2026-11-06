using System.Globalization;
using AresToys.ImageEffects.Drawing;
using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Drawings;

/// <summary>Effect inspired by ShareX. Watermark
/// text with optional outline + drop-shadow, anchored at one of nine canvas positions. The
/// <see cref="Font"/> property accepts the legacy ShareX format <c>"Family, Size[unit][, style]"</c>
/// (e.g. <c>"Arial, 36pt"</c>, <c>"Calibri, 18pt, style=Bold"</c>) which we parse on apply.
/// Independent gradients on text / outline / shadow each fall back to the solid colour when
/// their respective <c>UseGradient</c> toggle is off.</summary>
public sealed class DrawTextExImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_text_ex";
    public override string Name => "Text";

    public string Text { get; set; } = "Text";
    public TextPlacement Placement { get; set; } = TextPlacement.TopLeft;

    [EffectParameter(-2000, 2000, DisplayName = "Offset X")]
    public int OffsetX { get; set; }

    [EffectParameter(-2000, 2000, DisplayName = "Offset Y")]
    public int OffsetY { get; set; }

    [EffectParameter(-360, 360, DisplayName = "Angle")]
    public int Angle { get; set; }

    public bool AutoHide { get; set; }

    /// <summary>Font descriptor in ShareX legacy format (<c>"Arial, 36pt"</c>). Parsed on
    /// apply rather than on set so the user can paste a new value via the textbox without
    /// having to manage a typed parser through the binding.</summary>
    public string Font { get; set; } = "Arial, 36pt";

    public SKColor Color { get; set; } = new(235, 235, 235);
    public bool UseGradient { get; set; }
    public GradientInfo Gradient { get; set; } = new();

    public bool Outline { get; set; }
    [EffectParameter(0, 50, DisplayName = "Outline size")]
    public int OutlineSize { get; set; } = 5;
    public SKColor OutlineColor { get; set; } = new(235, 0, 0);
    public bool OutlineUseGradient { get; set; }
    public GradientInfo OutlineGradient { get; set; } = new();

    public bool Shadow { get; set; }
    [EffectParameter(-50, 50, DisplayName = "Shadow X")]
    public int ShadowOffsetX { get; set; }
    [EffectParameter(-50, 50, DisplayName = "Shadow Y")]
    public int ShadowOffsetY { get; set; } = 5;
    public SKColor ShadowColor { get; set; } = new(0, 0, 0, 125);
    public bool ShadowUseGradient { get; set; }
    public GradientInfo ShadowGradient { get; set; } = new();

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(Text)) return source.Copy();

        // ShareX-compatible placeholder expansion. Imported .sxie presets store templates like
        // "%y-%mo-%d" that need to evaluate at render time — without this they get drawn
        // verbatim. Covers the common date/time + width/height tokens; literal % is escaped
        // via "%%". Out-of-set tokens fall through unchanged so we don't garble user text.
        var resolvedText = ExpandPlaceholders(Text, source.Width, source.Height);

        var (family, size, style) = ParseFont(Font);
        using var typeface = SKTypeface.FromFamilyName(family, style);
        using var font = new SKFont(typeface, size);

        // Measure the text bounds once — used both for AutoHide and to anchor the rotated
        // string at its top-left, regardless of which Placement was picked.
        font.MeasureText(resolvedText, out var bounds);
        if (AutoHide && (bounds.Width > source.Width || bounds.Height > source.Height))
            return source.Copy();

        var anchor = ResolveAnchor(source.Width, source.Height, bounds.Width, bounds.Height);

        var result = source.Copy();
        using var canvas = new SKCanvas(result);
        canvas.Save();
        canvas.Translate(anchor.X, anchor.Y);
        if (Angle != 0) canvas.RotateDegrees(Angle);

        // Shader sized to the glyph bounds — used by all three passes (fill / outline / shadow)
        // when their respective UseGradient toggle is on. Computed once because all three need
        // the same dimensions; the shader itself is per-paint (Skia doesn't allow sharing).
        var shaderWidth = (int)bounds.Width + 1;
        var shaderHeight = (int)bounds.Height + 1;

        // Drop shadow first so it sits behind the fill + outline.
        if (Shadow)
        {
            using var shadowPaint = new SKPaint { IsAntialias = true, Color = ShadowColor };
            if (ShadowUseGradient && ShadowGradient.IsValid)
                shadowPaint.Shader = ShadowGradient.CreateShader(shaderWidth, shaderHeight);
            canvas.DrawText(resolvedText, ShadowOffsetX, -bounds.Top + ShadowOffsetY, font, shadowPaint);
        }

        // Outline pass under the fill so the stroke shows around the glyphs.
        if (Outline && OutlineSize > 0)
        {
            using var outlinePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = OutlineSize,
                Color = OutlineColor,
            };
            if (OutlineUseGradient && OutlineGradient.IsValid)
                outlinePaint.Shader = OutlineGradient.CreateShader(shaderWidth, shaderHeight);
            canvas.DrawText(resolvedText, 0, -bounds.Top, font, outlinePaint);
        }

        using var fillPaint = new SKPaint { IsAntialias = true, Color = Color };
        if (UseGradient && Gradient.IsValid)
            fillPaint.Shader = Gradient.CreateShader(shaderWidth, shaderHeight);
        canvas.DrawText(resolvedText, 0, -bounds.Top, font, fillPaint);

        canvas.Restore();
        return result;
    }

    private SKPoint ResolveAnchor(int canvasWidth, int canvasHeight, float textWidth, float textHeight)
    {
        // Translate Placement → top-left position of the text rectangle, then apply Offset.
        // Offset is always "distance from the anchor edge" — so for *Right placements OffsetX
        // pushes LEFT (-X) and for Bottom* placements OffsetY pushes UP (-Y). Mirrors ShareX's
        // Helpers.GetPosition exactly so .sxie presets land where their authors intended.
        // Note: MiddleCenter follows ShareX's quirk of ignoring the offset entirely.
        switch (Placement)
        {
            case TextPlacement.TopLeft:
                return new SKPoint(OffsetX, OffsetY);
            case TextPlacement.TopCenter:
                return new SKPoint((canvasWidth - textWidth) / 2f, OffsetY);
            case TextPlacement.TopRight:
                return new SKPoint(canvasWidth - textWidth - OffsetX, OffsetY);
            case TextPlacement.MiddleLeft:
                return new SKPoint(OffsetX, (canvasHeight - textHeight) / 2f);
            case TextPlacement.MiddleCenter:
                return new SKPoint((canvasWidth - textWidth) / 2f, (canvasHeight - textHeight) / 2f);
            case TextPlacement.MiddleRight:
                return new SKPoint(canvasWidth - textWidth - OffsetX, (canvasHeight - textHeight) / 2f);
            case TextPlacement.BottomLeft:
                return new SKPoint(OffsetX, canvasHeight - textHeight - OffsetY);
            case TextPlacement.BottomCenter:
                return new SKPoint((canvasWidth - textWidth) / 2f, canvasHeight - textHeight - OffsetY);
            case TextPlacement.BottomRight:
                return new SKPoint(canvasWidth - textWidth - OffsetX, canvasHeight - textHeight - OffsetY);
            default:
                return new SKPoint(OffsetX, OffsetY);
        }
    }

    /// <summary>Expand ShareX-compatible placeholders in the watermark text. Templates like
    /// <c>%y-%mo-%d</c> evaluate against <see cref="DateTime.Now"/>; image-dimension tokens
    /// (<c>%width</c> / <c>%height</c>) evaluate against the source bitmap's pixels. Order of
    /// replacement matters — the longer tokens (<c>%mo</c>, <c>%mi</c>) must be replaced
    /// BEFORE the single-letter prefix subset they share (<c>%m</c>) or "month" gets eaten by
    /// the minute pattern. Literal <c>%%</c> stays as a single <c>%</c>. Unknown tokens fall
    /// through unchanged so user-typed text isn't mangled.</summary>
    private static string ExpandPlaceholders(string template, int sourceWidth, int sourceHeight)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('%', StringComparison.Ordinal))
            return template;
        var now = DateTime.Now;
        // Long-form tokens MUST replace before any prefix subset. e.g. "%h" eats the "h" in
        // "%height" if the single-letter pattern runs first; "%mo" must beat "%m" (we don't
        // expose %m, but the rule still matters if it gets added). Order from longest token
        // to shortest within each prefix family.
        return template
            // Date / time
            .Replace("%yyyy", now.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%yy", (now.Year % 100).ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%y", now.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%mo", now.Month.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%mi", now.Minute.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%d", now.Day.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%s", now.Second.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            // Image metadata — `%width` / `%height` BEFORE `%h` so the latter doesn't eat
            // letters out of the former. `%w` similarly comes after `%width`.
            .Replace("%width", sourceWidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%height", sourceHeight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%h", now.Hour.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%w", sourceWidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            // Process / user / host names — synthesised from the live process running AresToys.
            // Mostly useful in screenshot-app contexts but kept for ShareX preset parity.
            .Replace("%un", Environment.UserName, StringComparison.Ordinal)
            .Replace("%hn", Environment.MachineName, StringComparison.Ordinal)
            // Literal % escape — replaced last so we don't expand "%%y" as "%2026".
            .Replace("%%", "%", StringComparison.Ordinal);
    }

    /// <summary>Parse a ShareX legacy font descriptor (<c>"Arial, 36pt"</c>). Returns family
    /// name, size in pixels, and a coarse SKFontStyle flag set. Unknown styles fall back to
    /// regular weight at the requested size.</summary>
    private static (string Family, float Size, SKFontStyle Style) ParseFont(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("Arial", 36f, SKFontStyle.Normal);
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var family = parts.Length > 0 ? parts[0] : "Arial";
        var size = 36f;
        if (parts.Length > 1)
        {
            // Accept "36pt", "36", "36.5pt" — strip non-digits beyond the first decimal.
            var sizeStr = new string(parts[1].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            if (float.TryParse(sizeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                // ShareX writes "pt" (1 pt = 1.333 px at 96 DPI); SkiaSharp font sizes are in
                // pixels, so convert. Bare numbers (no "pt") are assumed already-pixel.
                size = parts[1].Contains("pt", StringComparison.OrdinalIgnoreCase) ? parsed * 96f / 72f : parsed;
            }
        }
        var style = SKFontStyle.Normal;
        for (var i = 2; i < parts.Length; i++)
        {
            var s = parts[i].ToLowerInvariant();
            if (s.Contains("bold") && s.Contains("italic")) style = SKFontStyle.BoldItalic;
            else if (s.Contains("bold")) style = SKFontStyle.Bold;
            else if (s.Contains("italic")) style = SKFontStyle.Italic;
        }
        return (family, size, style);
    }
}
