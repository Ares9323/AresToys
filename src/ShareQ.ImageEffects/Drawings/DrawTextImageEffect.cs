using System.Globalization;
using ShareQ.ImageEffects.Drawing;
using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Drawings;

/// <summary>Ported from ShareX (GPL v3) — ImageEffectsLib/Drawings/DrawText.cs. Text watermark
/// with optional rounded background box, border, and drop-shadow — distinct from
/// <see cref="DrawTextExImageEffect"/> which is text-only with outline + per-glyph shadow.
/// Used by the Windows*/ActivateWindows/DiscordSpoiler/TwitchLive presets which paint a
/// "speech bubble" style label.</summary>
public sealed class DrawTextImageEffect : DrawingImageEffectBase
{
    public override string Id => "draw_text";
    public override string Name => "Text watermark";

    public string Text { get; set; } = "Text watermark";
    public TextPlacement Placement { get; set; } = TextPlacement.BottomRight;

    [EffectParameter(-2000, 2000, DisplayName = "Offset X")]
    public int OffsetX { get; set; } = 5;

    [EffectParameter(-2000, 2000, DisplayName = "Offset Y")]
    public int OffsetY { get; set; } = 5;

    public bool AutoHide { get; set; }

    /// <summary>Font descriptor in ShareX legacy format (<c>"Arial, 11.25pt"</c>). Same parser
    /// as DrawTextEx — handled at apply-time so the textbox can hold raw user input.</summary>
    public string TextFont { get; set; } = "Arial, 11.25pt";

    public SKColor TextColor { get; set; } = new(235, 235, 235);

    public bool DrawTextShadow { get; set; } = true;
    public SKColor TextShadowColor { get; set; } = SKColors.Black;
    [EffectParameter(-50, 50, DisplayName = "Text shadow X")]
    public int TextShadowOffsetX { get; set; } = -1;
    [EffectParameter(-50, 50, DisplayName = "Text shadow Y")]
    public int TextShadowOffsetY { get; set; } = -1;

    [EffectParameter(0, 100, DisplayName = "Corner radius")]
    public int CornerRadius { get; set; } = 4;

    public Padding Padding { get; set; } = new(5, 5, 5, 5);

    public bool DrawBorder { get; set; } = true;
    public SKColor BorderColor { get; set; } = SKColors.Black;
    [EffectParameter(0, 50, DisplayName = "Border size")]
    public int BorderSize { get; set; } = 1;

    public bool DrawBackground { get; set; } = true;
    public SKColor BackgroundColor { get; set; } = new(42, 47, 56);

    public bool UseGradient { get; set; }
    public GradientInfo Gradient { get; set; } = new();

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(Text)) return source.Copy();

        var (family, size, style) = ParseFont(TextFont);
        using var typeface = SKTypeface.FromFamilyName(family, style);
        using var font = new SKFont(typeface, size);
        font.MeasureText(Text, out var bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) return source.Copy();

        // Watermark box = padded text rectangle. Anchor in canvas using ShareX's compass-style
        // sign convention (Bottom*/Right* placements subtract Offset from the corner).
        var textWidth = (int)Math.Ceiling(bounds.Width);
        var textHeight = (int)Math.Ceiling(bounds.Height);
        var boxWidth = Padding.Left + textWidth + Padding.Right;
        var boxHeight = Padding.Top + textHeight + Padding.Bottom;
        var anchor = ResolvePlacement(source.Width, source.Height, boxWidth, boxHeight);

        if (AutoHide && (anchor.X < 0 || anchor.Y < 0
            || anchor.X + boxWidth > source.Width || anchor.Y + boxHeight > source.Height))
            return source.Copy();

        var result = source.Copy();
        using var canvas = new SKCanvas(result);

        var rect = new SKRect(anchor.X, anchor.Y, anchor.X + boxWidth, anchor.Y + boxHeight);
        var radius = Math.Max(0, CornerRadius);

        // Background fill — solid colour or gradient bound to the box rectangle so the
        // gradient axis runs across the watermark, not the whole canvas.
        if (DrawBackground)
        {
            using var bgPaint = new SKPaint { IsAntialias = true, Color = BackgroundColor };
            if (UseGradient && Gradient.IsValid)
            {
                // CreateShader anchors the gradient at (0,0); translate it onto the watermark
                // box so the gradient axis runs across the box rather than across the canvas.
                using var shader = Gradient.CreateShader(boxWidth, boxHeight);
                bgPaint.Shader = shader.WithLocalMatrix(SKMatrix.CreateTranslation(anchor.X, anchor.Y));
            }
            canvas.DrawRoundRect(rect, radius, radius, bgPaint);
        }

        // Border stroke — sits on the rounded rect's edge; insetting by half the stroke width
        // keeps the stroke centred on the boundary instead of bleeding past it.
        if (DrawBorder && BorderSize > 0)
        {
            var inset = BorderSize / 2f;
            var strokeRect = new SKRect(rect.Left + inset, rect.Top + inset,
                rect.Right - inset, rect.Bottom - inset);
            using var borderPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = BorderSize,
                Color = BorderColor,
            };
            canvas.DrawRoundRect(strokeRect, radius, radius, borderPaint);
        }

        // Text + optional drop shadow. Y = anchor.Y + Padding.Top - bounds.Top puts the visual
        // top of the glyphs at anchor.Y + Padding.Top (bounds.Top is negative for ascenders).
        var textX = anchor.X + Padding.Left;
        var textY = anchor.Y + Padding.Top - bounds.Top;

        if (DrawTextShadow)
        {
            using var shadowPaint = new SKPaint { IsAntialias = true, Color = TextShadowColor };
            canvas.DrawText(Text, textX + TextShadowOffsetX, textY + TextShadowOffsetY, font, shadowPaint);
        }

        using var textPaint = new SKPaint { IsAntialias = true, Color = TextColor };
        canvas.DrawText(Text, textX, textY, font, textPaint);
        return result;
    }

    private SKPointI ResolvePlacement(int canvasW, int canvasH, int boxW, int boxH)
    {
        switch (Placement)
        {
            case TextPlacement.TopLeft: return new SKPointI(OffsetX, OffsetY);
            case TextPlacement.TopCenter: return new SKPointI((canvasW - boxW) / 2, OffsetY);
            case TextPlacement.TopRight: return new SKPointI(canvasW - boxW - OffsetX, OffsetY);
            case TextPlacement.MiddleLeft: return new SKPointI(OffsetX, (canvasH - boxH) / 2);
            case TextPlacement.MiddleCenter: return new SKPointI((canvasW - boxW) / 2, (canvasH - boxH) / 2);
            case TextPlacement.MiddleRight: return new SKPointI(canvasW - boxW - OffsetX, (canvasH - boxH) / 2);
            case TextPlacement.BottomLeft: return new SKPointI(OffsetX, canvasH - boxH - OffsetY);
            case TextPlacement.BottomCenter: return new SKPointI((canvasW - boxW) / 2, canvasH - boxH - OffsetY);
            case TextPlacement.BottomRight: return new SKPointI(canvasW - boxW - OffsetX, canvasH - boxH - OffsetY);
            default: return new SKPointI(OffsetX, OffsetY);
        }
    }

    /// <summary>Same legacy-format font parser DrawTextEx uses. Accepts <c>"Family, Size[unit][, style]"</c>;
    /// pt is converted to px at 96 DPI, bare numbers are assumed already-pixel.</summary>
    private static (string Family, float Size, SKFontStyle Style) ParseFont(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("Arial", 15f, SKFontStyle.Normal);
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var family = parts.Length > 0 ? parts[0] : "Arial";
        var size = 15f;
        if (parts.Length > 1)
        {
            var sizeStr = new string(parts[1].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            if (float.TryParse(sizeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                size = parts[1].Contains("pt", StringComparison.OrdinalIgnoreCase) ? parsed * 96f / 72f
                     : parts[1].Contains("px", StringComparison.OrdinalIgnoreCase) ? parsed
                     : parsed;
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
