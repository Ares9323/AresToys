using SkiaSharp;

namespace AresToys.App.Services.ImageEffects;

/// <summary>Builds a synthetic 800×600 image used as the default preview canvas in the
/// effects editor. We generate it procedurally at startup instead of bundling a PNG so the
/// asset survives any future relocation and the file size stays out of the installer. The
/// content is deliberately varied — gradient, primary-coloured shapes, text, sharp edges —
/// so adjustments / filters / manipulations all show a visible delta.</summary>
public static class SampleImageGenerator
{
    public static SKBitmap Build(int width = 800, int height = 600)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        // Background: vertical gradient from a deep teal to a warm coral so saturation /
        // hue / brightness changes are immediately visible.
        using (var bgPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                new[] { new SKColor(0x1E, 0x40, 0x5C), new SKColor(0xD9, 0x5B, 0x4A) },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(0, 0, width, height, bgPaint);
        }

        // Three solid-colour discs — primary RGB so colour-balance / channel tweaks are easy
        // to see. Discs overlap deliberately to expose any blending change.
        var radius = height * 0.18f;
        DrawDisc(canvas, width * 0.30f, height * 0.40f, radius, new SKColor(0xE8, 0x4A, 0x4A));
        DrawDisc(canvas, width * 0.50f, height * 0.40f, radius, new SKColor(0x4A, 0xC0, 0x6B));
        DrawDisc(canvas, width * 0.70f, height * 0.40f, radius, new SKColor(0x4A, 0x7A, 0xE8));

        // White checkerboard strip at the top — high-contrast region for sharpen / blur diff.
        const int squareSize = 24;
        for (var x = 0; x < width; x += squareSize)
        {
            using var sqPaint = new SKPaint
            {
                Color = ((x / squareSize) % 2 == 0) ? SKColors.White : new SKColor(0x20, 0x20, 0x20),
            };
            canvas.DrawRect(x, 0, squareSize, squareSize * 2, sqPaint);
        }

        // Text label at the bottom — letterforms are great for testing edge-detect / emboss.
        using (var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
        })
        using (var font = new SKFont
        {
            Size = 56,
            Embolden = true,
        })
        {
            const string text = "AresToys Sample";
            var bounds = new SKRect();
            font.MeasureText(text, out bounds);
            canvas.DrawText(text, (width - bounds.Width) / 2, height - 60, font, textPaint);
        }

        return bitmap;
    }

    private static void DrawDisc(SKCanvas canvas, float cx, float cy, float r, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r, paint);
    }
}
